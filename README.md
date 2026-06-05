```python
import os
import json
import math
import time
import os
from http.server import BaseHTTPRequestHandler, HTTPServer
from socketserver import ThreadingMixIn
import threading
import urllib.parse
from RSBot import *


# Plugin Metadata
NAME = "OpenSilkroadMap-Explorer"
DESCRIPTION = "Logs linear movement and teleports (including NPC/portals) to a navigation graph format to be used for A* navigation."
AUTHOR = "Egezenn"
VERSION = "1.0.0"


# --- Coordinate Utilities ---
def get_safe_pos():
    """Returns character position as a dict {x, y, z, region} with normalized types."""
    try:
        # get_position is a built-in RSBot function
        pos = get_position()
        if not pos:
            return None

        if isinstance(pos, dict):
            return {
                "x": float(pos.get("x", 0)),
                "y": float(pos.get("y", 0)),
                "z": float(pos.get("z", 0)),
                "region": int(pos.get("region", 0)),
            }

        return {
            "x": float(getattr(pos, "x", getattr(pos, "X", 0))),
            "y": float(getattr(pos, "y", getattr(pos, "Y", 0))),
            "z": float(getattr(pos, "z", getattr(pos, "Z", 0))),
            "region": int(getattr(pos, "region", getattr(pos, "Region", 0))),
        }
    except Exception as e:
        return None


def get_semantic_name(pos):
    """Generates a consistent node ID string."""
    if not pos:
        return "unknown"
    # Ensure pos is a dict for calculation
    p = pos if isinstance(pos, dict) else get_safe_pos()
    if not p or not isinstance(p, dict):
        return "unknown"

    # Match JS Math.floor logic for stable IDs. Your provided data
    # (e.g. -11444.5 -> -11445) confirms floor-based rounding is standard.
    x = int(math.floor(p.get("x", 0)))
    y = int(math.floor(p.get("y", 0)))
    r = int(p.get("region", 0))
    return f"{x}_{y}_{r}"


def get_dist(p1, p2):
    """Calculates Euclidean distance between two points, handling region boundaries safely."""
    if not p1 or not p2:
        return float("inf")
    if int(p1.get("region", 0)) != int(p2.get("region", 0)):
        return float("inf")
    return math.sqrt((p1["x"] - p2["x"]) ** 2 + (p1["y"] - p2["y"]) ** 2)


# NOTE: move_to, teleport, get_teleport_data, and get_gateways
# are provided natively by the RSBot environment. Do not redefine.

# State
is_running = False
is_nav_paused = False
prev_pos = None
start_pos = None
is_moving = False
manual_start_node = None

# Teleport State
teleport_start_pos = None
is_session_start = False

pending_teleport_data = None  # Stores { 'npc': ..., 'dest': ... } during jump
cached_node_ids = []
# State for navigation and bridging
linkage_data = {"nodes": {}, "edges": {}}
linkage_version = time.time()
active_path = []
last_update_pos = None
data_file = "navigation_linkage.json"
gateway_server = None
gateway_active = False
console_logs = []
nav_error = None
pending_nav_id = None
current_bot_status = {}  # Thread-safe copy updated by main loop

# Stall Detection
last_stall_check_time = 0
last_stall_check_pos = None
last_teleport_time = 0

# Native Gate Network (Reference Data)
native_gateways = {}  # { teleport_id: { x, y, region, links: [...] } }


# GUI Initialization
gui = GUI(NAME)


def debug(msg):
    global console_logs
    formatted = f"[{time.strftime('%H:%M:%S')}] {msg}"
    console_logs.append(formatted)
    if len(console_logs) > 50:
        console_logs.pop(0)
    log(formatted)


def on_start_clicked():
    global is_running, is_session_start, prev_pos
    is_running = True
    is_session_start = True
    prev_pos = None
    btn_start.set_enabled(False)
    btn_stop.set_enabled(True)
    debug("Logging started.")


def on_stop_clicked():
    global is_running, is_moving, start_pos, manual_start_node, active_path, prev_pos
    is_running = False
    is_moving = False
    start_pos = None
    prev_pos = None
    manual_start_node = None
    active_path = []
    btn_start.set_enabled(True)
    btn_stop.set_enabled(False)
    debug("Logging stopped.")
    save_data()


def on_clear_nav_clicked():
    global linkage_data
    linkage_data = {"nodes": {}, "edges": {}}
    save_data()
    debug("Navigation data cleared.")


def on_force_heal_changed(checked):
    pass


def on_load_clicked():
    load_data()
    debug("Navigation data reloaded from file.")


class NavHandler(BaseHTTPRequestHandler):
    def do_OPTIONS(self):
        self.send_response(200)
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
        self.send_header(
            "Access-Control-Allow-Headers", "Content-Type, X-Requested-With"
        )
        self.end_headers()

    def do_GET(self):
        global pending_nav_id, is_nav_paused
        parsed_path = urllib.parse.urlparse(self.path)

        if parsed_path.path == "/shutdown":
            self.send_response(200)
            self.send_header("Access-Control-Allow-Origin", "*")
            self.end_headers()
            self.wfile.write(b"Shutting down")
            threading.Thread(target=self.server.shutdown, daemon=True).start()
            return

        if parsed_path.path == "/data":
            self.send_response(200)
            self.send_header("Access-Control-Allow-Origin", "*")
            self.send_header("Content-type", "application/json")
            response_data = json.dumps(linkage_data).encode()
            self.send_header("Content-Length", str(len(response_data)))
            self.end_headers()
            self.wfile.write(response_data)
            return

        if parsed_path.path == "/navigate":
            query = urllib.parse.parse_qs(parsed_path.query)
            node_id = query.get("id", [None])[0]

            # Coordination check for "Move to Anywhere"
            x = query.get("x", [None])[0]
            y = query.get("y", [None])[0]

            region = query.get("region", [None])[0]

            if x and y and region:
                try:
                    # Robust parsing for coordinates
                    safe_x = float(x)
                    safe_y = float(y)

                    safe_region = int(region)

                    pos = {"x": safe_x, "y": safe_y, "region": safe_region}

                    direct = query.get("direct", ["false"])[0].lower() == "true"
                    if direct:
                        debug(
                            f"[HTTP] Calculating DIRECT path to ({pos['x']}, {pos['y']})..."
                        )
                        curr_pos = get_safe_pos()
                        # Bridge from current position to target (ignores graph)
                        log_segment(curr_pos, pos)
                        node_id = get_semantic_name(pos)
                        save_data()
                    else:
                        debug(
                            f"[HTTP] Bridging from graph to ({pos['x']}, {pos['y']}) for navigation..."
                        )
                        # Find the entry point in the existing graph
                        bridge_start_id = find_nearest_node(pos)
                        if bridge_start_id:
                            # Create a chunked path from that node to our final destination
                            start_node_data = linkage_data["nodes"][bridge_start_id]
                            log_segment(start_node_data, pos, node_a_id=bridge_start_id)
                            # The final node ID is the one generated for our target position
                            node_id = get_semantic_name(pos)
                            save_data()
                        else:
                            # If no graph exists yet, just use the point itself
                            node_id = get_semantic_name(pos)
                            if node_id not in linkage_data["nodes"]:
                                linkage_data["nodes"][node_id] = pos
                                save_data()
                except ValueError as ve:
                    debug(f"[HTTP] Invalid coordinate data: {ve}")
                    node_id = None

            if node_id:
                pending_nav_id = node_id
                self.send_response(200)
                self.send_header("Access-Control-Allow-Origin", "*")
                self.send_header("Content-type", "text/plain")
                self.end_headers()
                self.wfile.write(f"Navigation to {node_id} initiated".encode())
                return

        if parsed_path.path == "/status":
            self.send_response(200)
            self.send_header("Access-Control-Allow-Origin", "*")
            self.send_header("Content-type", "application/json")

            try:
                # Return the last known status updated by the main bot thread
                response_json = json.dumps(current_bot_status).encode()
                self.send_header("Content-Length", str(len(response_json)))
                self.end_headers()
                self.wfile.write(response_json)
            except Exception as e:
                debug(f"[HTTP] Status serialization error: {e}")
                self.send_response(500)
                self.end_headers()
            return

        if parsed_path.path == "/gateways":
            self.send_response(200)
            self.send_header("Access-Control-Allow-Origin", "*")
            self.send_header("Content-type", "application/json")
            response_data = json.dumps(native_gateways).encode()
            self.send_header("Content-Length", str(len(response_data)))
            self.end_headers()
            self.wfile.write(response_data)
            return

        if parsed_path.path == "/nav/stop":
            is_nav_paused = True
            curr_pos = get_safe_pos()
            if curr_pos:
                move_to(curr_pos["x"], curr_pos["y"], int(curr_pos.get("region", 0)))
            self.send_response(200)
            self.send_header("Access-Control-Allow-Origin", "*")
            self.end_headers()
            self.wfile.write(b"Navigation paused")
            debug("Navigation paused remotely.")
            return

        if parsed_path.path == "/nav/resume":
            is_nav_paused = False
            self.send_response(200)
            self.send_header("Access-Control-Allow-Origin", "*")
            self.end_headers()
            self.wfile.write(b"Navigation resumed")
            debug("Navigation resumed remotely.")
            return

        self.send_response(404)
        self.end_headers()

    def do_POST(self):
        global linkage_data
        parsed_path = urllib.parse.urlparse(self.path)

        if parsed_path.path == "/data":
            content_length = int(self.headers["Content-Length"])
            post_data = self.rfile.read(content_length)

            try:
                new_data = json.loads(post_data.decode("utf-8"))
                if "nodes" in new_data and "edges" in new_data:
                    linkage_data = new_data
                    save_data()

                    self.send_response(200)
                    self.send_header("Access-Control-Allow-Origin", "*")
                    self.send_header("Content-type", "application/json")
                    self.end_headers()
                    self.wfile.write(
                        json.dumps(
                            {
                                "status": "success",
                                "message": "Linkage data updated and saved",
                            }
                        ).encode()
                    )
                    debug("[HTTP] Linkage data updated from web.")
                else:
                    self.send_response(400)
                    self.send_header("Access-Control-Allow-Origin", "*")
                    self.end_headers()
                    self.wfile.write(
                        b"Invalid data format. Must contain 'nodes' and 'edges'."
                    )
            except Exception as e:
                self.send_response(500)
                self.send_header("Access-Control-Allow-Origin", "*")
                self.end_headers()
                self.wfile.write(f"Error processing data: {e}".encode())
                debug(f"[HTTP] Failed to update linkage data: {e}")
            return

        if parsed_path.path == "/import":
            content_length = int(self.headers["Content-Length"])
            post_data = self.rfile.read(content_length)
            try:
                data = json.loads(post_data.decode("utf-8"))
                gate_id = str(data.get("gate_id"))
                link_idx = int(data.get("link_index"))

                if gate_id in native_gateways:
                    gate = native_gateways[gate_id]
                    if 0 <= link_idx < len(gate["links"]):
                        link = gate["links"][link_idx]

                        # Add nodes and edge
                        node_a_id = get_semantic_name(gate)
                        # Create virtual node pos for semantic name
                        dest_pos = {
                            "x": link["x"],
                            "y": link["y"],
                            "region": link["region"],
                        }
                        node_b_id = get_semantic_name(dest_pos)

                        log_segment(
                            gate,
                            dest_pos,
                            edge_type="teleport",
                            npc=gate.get("name"),
                            dest=link.get("target_id"),
                        )
                        save_data()

                        self.send_response(200)
                        self.send_header("Access-Control-Allow-Origin", "*")
                        self.send_header("Content-type", "application/json")
                        self.end_headers()
                        self.wfile.write(json.dumps({"status": "success"}).encode())
                        debug(
                            f"[HTTP] Imported native link: {gate['name']} -> {link.get('region')}"
                        )
                        return

                self.send_response(400)
                self.send_header("Access-Control-Allow-Origin", "*")
                self.end_headers()
            except Exception as e:
                self.send_response(500)
                self.send_header("Access-Control-Allow-Origin", "*")
                self.end_headers()
                debug(f"[HTTP] Import error: {e}")
            return

        self.send_response(404)
        self.end_headers()

    def log_message(self, format, *args):
        pass


def on_gateway_clicked():
    if gateway_active:
        stop_gateway()
    else:
        start_gateway()


class ThreadedHTTPServer(ThreadingMixIn, HTTPServer):
    allow_reuse_address = True


def start_gateway():
    global gateway_server, gateway_active

    def run_server():
        global gateway_active
        try:
            gateway_server.serve_forever()
        except Exception as e:
            debug(f"HTTP Server runtime error: {e}")
            gateway_active = False

    try:
        gateway_server = ThreadedHTTPServer(("127.0.0.1", 5588), NavHandler)
        threading.Thread(target=run_server, daemon=True).start()
        gateway_active = True
        btn_gateway.set_text("Stop Gateway")
        debug("Hardened gateway started on 127.0.0.1:5588")
    except Exception as e:
        debug(f"Failed to start gateway: {e}")


def stop_gateway():
    global gateway_server, gateway_active
    if gateway_server:
        gateway_server.shutdown()
        gateway_server.server_close()
        gateway_server = None
    gateway_active = False
    btn_gateway.set_text("Start Gateway")
    debug("Remote navigation gateway stopped.")


def cleanup_old_gateway():
    import http.client

    try:
        conn = http.client.HTTPConnection("127.0.0.1", 5588, timeout=1)
        conn.request("GET", "/shutdown")
        debug("Sent shutdown signal to old gateway instance.")
        time.sleep(0.5)
    except:
        pass


def on_teleport_request(dest_id, npc_codename):
    global pending_teleport_data
    if not is_running:
        return
    pending_teleport_data = {"dest": dest_id, "npc": npc_codename}
    debug(f"Capture Teleport Intent: {npc_codename} -> {dest_id}")


def on_teleported(state):
    global teleport_start_pos, prev_pos, pending_teleport_data, is_moving, start_pos
    if not is_running:
        return

    if state == 1:  # Teleport started
        # If we were moving, finalize the walk segment now
        if is_moving:
            debug("Finalizing walk segment before teleport...")
            end_pos = get_safe_pos()
            if start_pos and end_pos:
                log_segment(start_pos, end_pos)
            is_moving = False
            start_pos = None

        curr_pos = get_safe_pos()
        teleport_start_pos = curr_pos
        prev_pos = None
        debug(f"Teleport Sequence Started: {get_semantic_name(curr_pos)}")

    elif state == 2:  # Teleport completed
        curr_pos = get_safe_pos()
        debug(f"Teleport Jump Successful: {get_semantic_name(curr_pos)}")

        npc = pending_teleport_data["npc"] if pending_teleport_data else None
        dest = pending_teleport_data["dest"] if pending_teleport_data else None

        if teleport_start_pos and curr_pos:
            node_a_id = get_semantic_name(teleport_start_pos)
            node_b_id = get_semantic_name(curr_pos)
            debug(
                f"Teleport Edge Saved: {npc if npc else 'Portal'} ({node_a_id} -> {node_b_id})"
            )
            log_segment(
                teleport_start_pos, curr_pos, edge_type="teleport", npc=npc, dest=dest
            )
            save_data()

        teleport_start_pos = None
        pending_teleport_data = None
        prev_pos = curr_pos

        if len(active_path) >= 2:
            # Since we successfully teleported to active_path[1], advance path
            active_path.pop(0)
            if len(active_path) >= 2:
                # Trigger next step check in case we're in navigation mode
                move_to_next_waypoint()


x = 20
y = 20

gui.Label("OpenSilkroadMap-Explorer Controls", x, y, 200, 20)
y += 30
btn_start = gui.Button("Start Logging", x, y, 120, 30, handler=on_start_clicked)
btn_stop = gui.Button("Stop Logging", x + 130, y, 120, 30, handler=on_stop_clicked)
btn_stop.set_enabled(False)
btn_clear_nav = gui.Button(
    "Clear Nav Data", x + 260, y, 110, 30, handler=on_clear_nav_clicked
)
chk_force_heal = gui.CheckBox(
    "Force Self-Healing", x + 380, y + 5, 140, 20, handler=on_force_heal_changed
)
chk_force_heal.set_checked(True)

y += 50
gui.Label("Navigation Gateway & File IO", x, y, 250, 20)
y += 30
btn_load = gui.Button("Load from File", x, y, 120, 30, handler=on_load_clicked)
btn_gateway = gui.Button(
    "Start Gateway", x + 130, y, 120, 30, handler=on_gateway_clicked
)


# --- Navigation & Pathfinding ---


def find_nearest_node(pos):
    if not pos:
        return None
    nodes = linkage_data["nodes"]
    if not nodes:
        return None

    nearest_id = None
    min_dist = float("inf")

    # First pass: look for nodes in the same region
    for node_id, data in nodes.items():
        if data.get("region") == pos.get("region"):
            dist = math.sqrt((data["x"] - pos["x"]) ** 2 + (data["y"] - pos["y"]) ** 2)
            if dist < min_dist:
                min_dist = dist
                nearest_id = node_id

    # Fallback: if no nodes found in the same region, look globally
    # Note: Euclidean distance between local coordinates in different regions is physically wrong,
    # but it allows finding the 'logical' nearest point if the character is across a map boundary.
    if not nearest_id:
        for node_id, data in nodes.items():
            dist = math.sqrt((data["x"] - pos["x"]) ** 2 + (data["y"] - pos["y"]) ** 2)
            if dist < min_dist:
                min_dist = dist
                nearest_id = node_id

    return nearest_id


def calculate_path(start_id, target_id):
    nodes = linkage_data["nodes"]
    edges = linkage_data["edges"]

    # 1. Semantic Mapping: Map every raw ID to its simplified "X_Y_R" semantic name.
    # This unifies all 'session-based' nodes into shared physical locations.
    id_to_sid = {}
    sid_to_nids = {}  # Multiple records can share one semantic spot

    for nid, data in nodes.items():
        sid = get_semantic_name(data)
        id_to_sid[nid] = sid
        if sid not in sid_to_nids:
            sid_to_nids[sid] = []
        sid_to_nids[sid].append(nid)

    # 2. Build Adjacency in SEMANTIC space
    adj_sid = {sid: [] for sid in sid_to_nids}

    # 2a. explicit edges from data
    for edge in edges.values():
        u_raw, v_raw = edge["from"], edge["to"]
        if u_raw not in id_to_sid or v_raw not in id_to_sid:
            continue

        u_sid = id_to_sid[u_raw]
        v_sid = id_to_sid[v_raw]
        is_teleport = edge.get("type") == "teleport"

        # Store (neighbor_sid, is_teleport, from_nid, to_nid, edge_data)
        adj_sid[u_sid].append((v_sid, is_teleport, u_raw, v_raw, edge))
        if edge.get("type") == "walk":
            adj_sid[v_sid].append((u_sid, False, v_raw, u_raw, edge))

    # 2. Add edges

    def get_semantic_heuristic(a_sid, b_sid):
        a_nid = sid_to_nids[a_sid][0]
        # target_sid might be a virtual native node name, which is its own SID
        b_nid = sid_to_nids.get(b_sid, [b_sid])[0]
        if a_nid not in nodes or b_nid not in nodes:
            return 0
        a, b = nodes[a_nid], nodes[b_nid]
        if a.get("region") != b.get("region"):
            # Regions differ: Use 0 to enable Dijkstra mode across teleports.
            return 0
        return math.sqrt((a["x"] - b["x"]) ** 2 + (a["y"] - b["y"]) ** 2)

    start_sid = id_to_sid.get(start_id)
    target_sid = id_to_sid.get(target_id)

    # Fallback: if target_id was already a semantic ID, use it directly
    if not target_sid and target_id in sid_to_nids:
        target_sid = target_id

    if not start_sid or not target_sid:
        debug(
            f"Pathfinding failed: Start ({start_id}) or Target ({target_id}) not recognized."
        )
        return None

    open_set = {start_sid}
    came_from = {}
    g_score = {sid: float("inf") for sid in sid_to_nids}
    g_score[start_sid] = 0
    f_score = {sid: float("inf") for sid in sid_to_nids}
    f_score[start_sid] = get_semantic_heuristic(start_sid, target_sid)

    searched_count = 0
    while open_set:
        current_sid = min(open_set, key=lambda x: f_score[x])
        searched_count += 1

        if current_sid == target_sid:
            # Reconstruct RAW Path (Node, EdgeData) tuples
            path_tuples = []
            curr = current_sid
            skip_next = False
            while curr in came_from:
                prev_sid, u_raw, v_raw, edge_data = came_from[curr]
                
                is_teleport = edge_data and (
                    edge_data.get("type") == "teleport"
                    or edge_data.get("type") == "teleport_native"
                )
                
                if not skip_next:
                    path_tuples.append((v_raw, edge_data))
                else:
                    skip_next = False
                
                if is_teleport:
                    skip_next = True
                    
                curr = prev_sid
            path_tuples.append((start_id, None))
            path_tuples.reverse()

            debug(
                f"Path found: {len(path_tuples)} nodes (Semantic Search: {searched_count})"
            )
            return path_tuples

        open_set.remove(current_sid)

        # 1. Standard/Learned Neighbors
        for neighbor_sid, is_teleport, u_raw, v_raw, edge_data in adj_sid.get(
            current_sid, []
        ):
            weight = 100.0 if is_teleport else 5.0
            tentative_g_score = g_score[current_sid] + weight

            if tentative_g_score < g_score[neighbor_sid]:
                came_from[neighbor_sid] = (current_sid, u_raw, v_raw, edge_data)
                g_score[neighbor_sid] = tentative_g_score
                f_score[neighbor_sid] = tentative_g_score + get_semantic_heuristic(
                    neighbor_sid, target_sid
                )
                if neighbor_sid not in open_set:
                    open_set.add(neighbor_sid)

        # 2. Native Teleport Bridge Expansion
        for nid in sid_to_nids[current_sid]:
            for native_link in get_native_neighbors(nid):
                nsid = native_link[
                    "id"
                ]  # The native link ID is already a semantic name

                weight = native_link["weight"]
                tentative_g_score = g_score[current_sid] + weight

                if nsid not in g_score or tentative_g_score < g_score[nsid]:
                    # For native teleports, v_raw is the virtual destination ID
                    # Mark it as a teleport_native for the motor
                    edge_data_virtual = native_link.copy()
                    edge_data_virtual["type"] = "teleport_native"
                    came_from[nsid] = (
                        current_sid,
                        nid,
                        native_link["id"],
                        edge_data_virtual,
                    )
                    g_score[nsid] = tentative_g_score
                    f_score[nsid] = tentative_g_score + get_semantic_heuristic(
                        nsid, target_sid
                    )
                    if nsid not in open_set:
                        open_set.add(nsid)

    debug(f"Pathfinding failed: Searched {searched_count} semantic locations.")
    return None


def get_native_neighbors(node_id):
    """Checks if a node is near a native teleport gateway and returns destinations."""
    node = linkage_data["nodes"].get(node_id)
    if not node:
        return []

    neighbors = []
    # Cross-reference with global native gateways
    for tid, data in native_gateways.items():
        if str(data.get("region")) == str(node.get("region")):
            dist = math.sqrt(
                (data["x"] - node["x"]) ** 2 + (data["y"] - node["y"]) ** 2
            )
            if (
                dist < 25.0
            ):  # If the node is near the teleporter (matches web map snapping)
                for link in data["links"]:
                    # Create a temporary virtual node name for the destination
                    dest_id = f"{int(math.floor(link['x']))}_{int(math.floor(link['y']))}_{link['region']}"
                    # Add virtual node to linkage_data temporarily if missing so heuristic works
                    if dest_id not in linkage_data["nodes"]:
                        linkage_data["nodes"][dest_id] = {
                            "x": link["x"],
                            "y": link["y"],
                            "region": link["region"],
                            "is_virtual": True,
                        }

                    neighbors.append(
                        {
                            "id": dest_id,
                            "weight": 50,  # Teleports are fast but keep a small penalty for preference
                            "type": "teleport_native",
                            "tid": tid,
                            "target_id": link["target_id"],
                        }
                    )
    return neighbors


def move_to_next_waypoint():
    global active_path, nav_error, last_teleport_time
    if len(active_path) < 2:
        return

    # Path is now (node_id, edge_data) tuples
    start_node_id, _ = active_path[0]
    next_node_id, edge_data = active_path[1]

    # --- Teleport Execution Logic ---
    if edge_data and (
        edge_data.get("type") == "teleport"
        or edge_data.get("type") == "teleport_native"
    ):
        if edge_data.get("type") == "teleport":
            npc_name = str(edge_data.get("npc", "")).split("|")[0]
            dest_id = int(edge_data.get("dest"))
            debug(
                f"Teleport requested to {next_node_id} (NPC: {npc_name}, Dest: {dest_id}). Executing..."
            )
            if teleport(npc_name, dest_id):
                debug("Teleport command sent successfully.")
                last_teleport_time = time.time()
            else:
                debug("Teleport failed. Clearing path.")
                active_path = []
        else:
            npc_name = str(edge_data.get("tid", "")).split("|")[0]
            dest_id = int(edge_data.get("target_id"))
            debug(
                f"Native teleport requested to {next_node_id} (NPC: {npc_name}, Dest: {dest_id}). Executing..."
            )
            if teleport(npc_name, dest_id):
                debug("Teleport command sent successfully.")
                last_teleport_time = time.time()
            else:
                debug("Teleport failed. Clearing path.")
                active_path = []
        return

    next_node = linkage_data["nodes"][next_node_id]

    # --- Regular Walk Logic ---
    debug(f"Navigating to {next_node_id}")
    move_to(
        float(next_node["x"]), float(next_node["y"]), int(next_node.get("region", 0))
    )


# --- Core Logic ---


def load_data():
    global linkage_data
    path = os.path.join(os.path.dirname(get_config_dir()), "Data", data_file)
    if os.path.exists(path):
        try:
            with open(path, "r") as f:
                linkage_data = json.load(f)

            if not isinstance(linkage_data, dict):
                linkage_data = {"nodes": {}, "edges": {}}
            if "nodes" not in linkage_data:
                linkage_data["nodes"] = {}
            if "edges" not in linkage_data:
                linkage_data["edges"] = {}

            edited_count = sum(
                1 for n in linkage_data["nodes"].values() if n.get("edited")
            )
            debug(
                f"Loaded {len(linkage_data['nodes'])} nodes ({edited_count} edited) and {len(linkage_data['edges'])} edges from {data_file}."
            )
        except Exception as e:
            debug(f"Failed to load data: {e}")
            linkage_data = {"nodes": {}, "edges": {}}
    else:
        debug(f"No existing data file found at {path}")


def save_data():
    global linkage_version
    path = os.path.join(os.path.dirname(get_config_dir()), "Data", data_file)
    try:
        with open(path, "w") as f:
            json.dump(linkage_data, f, indent=4)
        linkage_version = time.time()
    except Exception as e:
        debug(f"Error saving linkage: {e}")


def log_segment(
    a, b, node_a_id=None, edge_type="walk", npc=None, dest=None, steps=None
):
    global is_session_start

    if node_a_id is None and a:
        if is_session_start:
            # Snap to nearest node if extremely close, otherwise use timestamp to avoid corruption
            nearest_stable = find_nearest_node(a)
            if nearest_stable:
                node_data = linkage_data["nodes"][nearest_stable]
                dist = math.sqrt(
                    (node_data["x"] - a["x"]) ** 2 + (node_data["y"] - a["y"]) ** 2
                )
                if dist < 2.0:
                    node_a_id = nearest_stable
                    debug(f"Connected to existing graph at: {node_a_id}")

            if not node_a_id:
                node_a_id = f"{get_semantic_name(a)}_{int(time.time())}"
                debug(f"Session started with fresh node: {node_a_id}")

            is_session_start = False
        else:
            node_a_id = get_semantic_name(a)

    # If the session started with a manual node, reset the flag now
    if node_a_id is not None and is_session_start:
        is_session_start = False

    node_b_id = get_semantic_name(b)

    if edge_type == "teleport":
        # Check for existing teleport edge between these nodes.
        if node_a_id and node_b_id:
            edge_id = f"{node_a_id}__{node_b_id}"
            if edge_id in linkage_data["edges"]:
                debug(f"Teleport edge {edge_id} already exists. Skipping duplicate.")
                return

    if node_a_id not in linkage_data["nodes"] and a:
        linkage_data["nodes"][node_a_id] = {
            "x": a["x"],
            "y": a["y"],
            "region": a["region"],
        }
    if node_b_id not in linkage_data["nodes"]:
        linkage_data["nodes"][node_b_id] = {
            "x": b["x"],
            "y": b["y"],
            "region": b["region"],
        }

    edge_id = f"{node_a_id}__{node_b_id}"
    if edge_id not in linkage_data["edges"]:
        # Linear Chunking: If Walk segment > 50 units, break it down
        if edge_type == "walk" and a and b:
            dx = b["x"] - a["x"]
            dy = b["y"] - a["y"]

            dist = math.sqrt(dx * dx + dy * dy)

            if dist > 50.0:
                num_chunks = int(
                    math.ceil(dist / 40.0)
                )  # Use 40 to ensure we're well under 50
                debug(
                    f"Interpolating {num_chunks} segments for long path ({dist:.1f} units)"
                )
                current_start_node = node_a_id

                for i in range(1, num_chunks):
                    ratio = i / float(num_chunks)
                    inter_pos = {
                        "x": a["x"] + dx * ratio,
                        "y": a["y"] + dy * ratio,
                        "region": a["region"],
                    }
                    inter_id = get_semantic_name(inter_pos)

                    # Log the sub-segment
                    log_segment(
                        None, inter_pos, node_a_id=current_start_node, edge_type="walk"
                    )
                    current_start_node = inter_id

                # Final sub-segment to point B
                log_segment(None, b, node_a_id=current_start_node, edge_type="walk")
                return

        linkage_data["edges"][edge_id] = {
            "from": node_a_id,
            "to": node_b_id,
            "type": edge_type,
            "npc": npc,
            "dest": dest,
            "steps": steps,
        }


def event_loop():
    global \
        prev_pos, \
        start_pos, \
        is_moving, \
        manual_start_node, \
        active_path, \
        pending_nav_id
    global last_stall_check_time, last_stall_check_pos, nav_error, last_teleport_time
    global current_bot_status

    # Update global status for HTTP server (Thread Safety)
    # Perform all bot-specific API calls here in the main thread
    current_path = list(active_path) if active_path else []
    current_bot_status = {
        "position": get_safe_pos(),
        "is_logging": is_running,
        "is_nav_paused": is_nav_paused,
        "data_version": linkage_version,
        "navigation": {
            "is_active": len(current_path) > 0,
            "target": current_path[-1][0] if current_path else None,
            "next_waypoint": current_path[1][0] if len(current_path) >= 2 else None,
            "remaining": len(current_path),
            "path": [p[0] for p in current_path],
            "error": nav_error,
        },
        "logs": list(console_logs),
        "is_gateway_active": gateway_active,
    }

    # Check for remote navigation request
    if pending_nav_id:
        nav_error = None
        target_id = pending_nav_id
        pending_nav_id = None
        if target_id in linkage_data["nodes"]:
            debug(f"[HTTP] Starting remote navigation to {target_id}")
            nodes = linkage_data["nodes"]
            edges = linkage_data["edges"]
            start_node_id = find_nearest_node(get_safe_pos())

            if start_node_id:
                path = calculate_path(start_node_id, target_id)
                if path:
                    active_path = path
                    debug(f"Path calculated: {len(path)} steps.")
                    # Initialize stall check
                    last_stall_check_time = time.time()
                    last_stall_check_pos = get_safe_pos()

                    move_to_next_waypoint()
                else:
                    nav_error = "path_not_found"
                    debug("No path found to remote target.")
            else:
                debug("Could not determine starting position for remote navigation.")
        else:
            debug(f"Remote target node {target_id} not found in data.")

    curr_pos = get_safe_pos()
    if curr_pos:
        # --- Ambient Healing & Verification ---
        # Automatically verify ANY node the character walks near (even without navigation)
        if chk_force_heal.get_checked():
            nearest_id = find_nearest_node(curr_pos)
            if nearest_id:
                node = linkage_data["nodes"][nearest_id]
                dist_to_node = math.sqrt(
                    (node["x"] - curr_pos["x"]) ** 2 + (node["y"] - curr_pos["y"]) ** 2
                )

                if dist_to_node < 2.0:
                    changed = False

                    # Verify node (remove edited status)
                    if "edited" in node:
                        del node["edited"]
                        changed = True
                        debug(f"Ambient Verification: Node {nearest_id}")

                    if changed:
                        save_data()

    if not is_nav_paused and len(active_path) >= 2 and curr_pos:
        # Smart Waypoint Skipping: Check up to 5 nodes ahead
        # This handles successful teleports or glitches automatically.
        advanced = False
        max_check = min(len(active_path), 6)

        for i in range(1, max_check):
            target_id, next_edge = active_path[i]
            target_node = linkage_data["nodes"].get(target_id)
            if not target_node:
                continue

            dist_to_target = math.sqrt(
                (target_node["x"] - curr_pos["x"]) ** 2
                + (target_node["y"] - curr_pos["y"]) ** 2
            )

            # Evaluate edge type to handle teleport scatter
            hit_radius = 2.5
            if next_edge and (
                next_edge.get("type") == "teleport"
                or next_edge.get("type") == "teleport_native"
            ):
                # Teleports can scatter characters up to 50 away from the raw node
                hit_radius = 60.0

            # Only evaluate distance if regions match OR it's extremely close, to prevent cross-dungeon geometry overlap
            valid_jump = False
            if dist_to_target < hit_radius:
                if (
                    str(target_node.get("region")) == str(curr_pos.get("region"))
                    or dist_to_target < 20.0
                ):
                    valid_jump = True

            # If we are near any future node, advance the path to it
            if valid_jump:
                if i > 1:
                    debug(f"Jump detected: skipping {i - 1} waypoints.")

                # Verify edges we traversed/jumped
                changed = False
                for k in range(i):
                    u_nid, _ = active_path[k]
                    v_nid, edge_data = (
                        active_path[k + 1] if k + 1 < len(active_path) else (None, None)
                    )
                    if v_nid and edge_data:
                        edge_id = f"{u_nid}__{v_nid}"
                        rev_id = f"{v_nid}__{u_nid}"
                        for eid in [edge_id, rev_id]:
                            if (
                                eid in linkage_data["edges"]
                                and "edited" in linkage_data["edges"][eid]
                            ):
                                del linkage_data["edges"][eid]["edited"]
                                changed = True
                                debug(f"Verified edge {eid} (removed edited status)")

                for _ in range(i):
                    active_path.pop(0)

                if changed:
                    save_data()

                advanced = True
                break

        if advanced:
            # Reset stall check when path advanced
            last_stall_check_time = time.time()
            last_stall_check_pos = curr_pos

            if len(active_path) < 2:
                debug("Navigation complete.")
                active_path = []
            else:
                move_to_next_waypoint()
        else:
            # Stall Detection Logic with Teleport Grace Period
            now = time.time()
            if now - last_teleport_time < 10.0:  # 10s grace after teleport
                last_stall_check_time = now  # Keep timer fresh

            if now - last_stall_check_time > 4.0:  # Check every 4 seconds
                if last_stall_check_pos:
                    dist_moved = math.sqrt(
                        (curr_pos["x"] - last_stall_check_pos["x"]) ** 2
                        + (curr_pos["y"] - last_stall_check_pos["y"]) ** 2
                    )
                    if dist_moved < 1.0:  # If moved less than 1 unit
                        debug("Navigation stall detected. Reissuing move command...")
                        move_to_next_waypoint()

                last_stall_check_time = now
                last_stall_check_pos = curr_pos

    if not is_running or teleport_start_pos is not None:
        return

    curr_pos = get_safe_pos()
    if not curr_pos:
        return

    if prev_pos is None:
        prev_pos = curr_pos
        return

    dist_sq = (curr_pos["x"] - prev_pos["x"]) ** 2 + (
        curr_pos["y"] - prev_pos["y"]
    ) ** 2

    # Safety: If the jump is too large (e.g. teleport/glitch), do not start a walk segment.
    # 100.0 sq units (10.0 units distance) is way more than a single pulse movement.
    if dist_sq > 100.0:
        prev_pos = curr_pos
        return

    moving_now = dist_sq > 0.01

    if not is_moving and moving_now:
        if manual_start_node:
            debug(f"Segment starting from manual anchor: {manual_start_node}")
        else:
            start_pos = prev_pos
        is_moving = True
    elif is_moving and not moving_now:
        end_pos = curr_pos
        is_moving = False
        if manual_start_node:
            log_segment(None, end_pos, node_a_id=manual_start_node)
            manual_start_node = None
        elif start_pos:
            log_segment(start_pos, end_pos)
        save_data()
    prev_pos = curr_pos


def load_native_teleport_data():
    global native_gateways
    native_gateways = {}
    debug("Native teleport data loading skipped (not supported by Python API).")


# Initialize
cleanup_old_gateway()
load_data()
load_native_teleport_data()
start_gateway()
```