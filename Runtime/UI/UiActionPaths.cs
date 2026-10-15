using System;

namespace Lumivale.Core.UI
{
    /// <summary>Project-defined action paths for the standard uGUI Input System module.</summary>
    [Serializable]
    public sealed class UiActionPaths
    {
        public string Navigate { get; set; } = "UI/Navigate";
        public string Point { get; set; } = "UI/Point";
        public string Click { get; set; } = "UI/Click";
        public string Submit { get; set; } = "UI/Submit";
        public string Cancel { get; set; } = "UI/Cancel";
        public string ScrollWheel { get; set; } = "UI/ScrollWheel";
    }
}
