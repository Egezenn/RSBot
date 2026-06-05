using System.Windows.Forms;
using RSBot.Core;
using RSBot.Core.Components;
using RSBot.Core.Plugins;

namespace RSBot.Skills
{
    public class SkillsView : IPluginView
    {
        public string InternalName => "RSBot.Skills";
        public string DisplayName => "Skills";
        public bool DisplayAsTab => true;
        public int Index => 1;
        public bool RequireIngame => true;
        public Control View => Views.View.Instance;

        public void Translate()
        {
            LanguageManager.Translate(View, Kernel.Language);
        }
    }
}
