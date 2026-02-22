using System;

namespace NovaTerminal.Core
{
    public class TabTemplateRule
    {
        public Guid ProfileId { get; set; }
        public string TemplateName { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
    }
}
