namespace Lumivale.Core.Settings
{
    /// <summary>
    /// Optional rendering extension to the display backend. Implementations belong to the project
    /// or render pipeline integration and must support reuse after Release.
    /// </summary>
    public interface IRenderingSettingsBackend : IGraphicsSettingsBackend
    {
        RenderingCapabilities RenderingCapabilities { get; }

        /// <summary>
        /// Returns supported rendering choices without applying or storing anything. Arguments are
        /// independent copies; fallback can be null while validating the configured defaults.
        /// Display, audio and language fields must remain unchanged.
        /// </summary>
        SettingsValues ValidateRendering(SettingsValues values, SettingsValues fallback);

        /// <summary>Applies the complete validated display and rendering snapshot.</summary>
        void Apply(SettingsValues values);

        /// <summary>Releases owned resources and restores native state; repeated calls are safe.</summary>
        void Release();
    }
}
