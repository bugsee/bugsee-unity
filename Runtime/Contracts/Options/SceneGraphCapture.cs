namespace Bugsee.Contracts.Options
{
    /// <summary>
    /// How much of the Unity scene the managed view hierarchy sends with a report (launch option
    /// <c>com.bugsee.option.unity.capture-scene-graph</c>, Unity only). The native
    /// <c>com.bugsee.option.capture.view-hierarchy</c> still gates the whole capture: with it off nothing is asked for.
    /// The option is stored in the launch options by its constant name (<c>"None"</c>, <c>"NamesOnly"</c>, <c>"All"</c>).
    /// </summary>
    public enum SceneGraphCapture
    {
        /// <summary>No managed tree: the report carries the native view hierarchy only.</summary>
        None = 0,

        /// <summary>
        /// The managed tree with object names and component type names, and no positioning data at all: no <c>bounds</c> on any node and
        /// no <c>scene</c> block. Names are still withheld under secure rectangles (positions are computed for that test and not sent).
        /// </summary>
        NamesOnly = 1,

        /// <summary>
        /// The default. The managed tree with names and 2D <c>bounds</c>, plus the <c>scene</c> block: the cameras and the 3D bounding
        /// boxes (position, orientation, size) of the visible scene objects, in metres. In an AR app that includes the device pose and
        /// the geometry of the AR planes and meshes the app renders.
        /// </summary>
        All = 2
    }

    /// <summary>Reads and writes <see cref="SceneGraphCapture"/> as it appears in the launch options.</summary>
    public static class SceneGraphCaptureOption
    {
        public const SceneGraphCapture Default = SceneGraphCapture.All;

        /// <summary>The value stored in the launch options (the constant name).</summary>
        public static string ToWire(SceneGraphCapture level)
        {
            switch (level)
            {
                case SceneGraphCapture.None: return "None";
                case SceneGraphCapture.NamesOnly: return "NamesOnly";
                case SceneGraphCapture.All: return "All";
                default: return ((int)level).ToString(System.Globalization.CultureInfo.InvariantCulture); // not a level: read back as one that sends nothing
            }
        }

        /// <summary>
        /// The level a launch-option value means. An absent value (<c>null</c>) is the default (<see cref="Default"/>). A value that is
        /// not one of the three names (a typo, an unknown number) is <see cref="SceneGraphCapture.None"/> and <paramref name="recognized"/>
        /// is false: an option that asks for less must never send more than the developer meant.
        /// </summary>
        public static SceneGraphCapture Parse(object value, out bool recognized)
        {
            recognized = true;
            if (value == null) return Default;
            if (value is SceneGraphCapture level)
            {
                if (level == SceneGraphCapture.None || level == SceneGraphCapture.NamesOnly || level == SceneGraphCapture.All) return level;
                recognized = false;
                return SceneGraphCapture.None;
            }

            var text = (value as string ?? string.Empty).Trim().Replace("_", string.Empty).Replace("-", string.Empty);
            if (string.Equals(text, "None", System.StringComparison.OrdinalIgnoreCase)) return SceneGraphCapture.None;
            if (string.Equals(text, "NamesOnly", System.StringComparison.OrdinalIgnoreCase)) return SceneGraphCapture.NamesOnly;
            if (string.Equals(text, "All", System.StringComparison.OrdinalIgnoreCase)) return SceneGraphCapture.All;
            recognized = false;
            return SceneGraphCapture.None;
        }
    }
}
