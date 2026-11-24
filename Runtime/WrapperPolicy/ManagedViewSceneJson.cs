using System;
using System.Globalization;
using System.Text;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Writes the <c>scene</c> value of the managed view hierarchy (specs: sdk/viewtree-scene, version 1). The tree is written first and
    /// the scene is best effort: the limits here (object count, characters, a deadline) stop the objects early and the document says
    /// so (<c>truncated</c>, or <c>skipped</c> when none was written), so a scene never costs the tree its reply.
    /// </summary>
    public static class ManagedViewSceneJson
    {
        /// <summary>Objects per reply. About 0.7 MB of JSON at the size of a typical row (the spec's open question on limits).</summary>
        public const int DefaultMaxObjects = 5000;
        /// <summary>Characters the scene may add to the reply; the whole <c>managed</c> document crosses the native bridge as one string.</summary>
        public const int DefaultMaxChars = 1000000;

        public const int Version = 1;
        public const string SelectionVisible = "visible";

        /// <summary>Appends <c>"scene":{...}</c> (with its leading comma) to <paramref name="sb"/>.</summary>
        /// <param name="expired">Asked every 64 objects; once true the remaining objects are left out.</param>
        public static void Write(
            StringBuilder sb, ManagedViewSceneDocument doc, int maxObjects, int maxChars, Func<bool> expired,
            out int sent, out bool truncated)
        {
            sent = 0;
            var start = sb.Length;
            sb.Append(",\"scene\":{\"version\":").Append(Version.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"selection\":\"").Append(SelectionVisible).Append('"');
            if (doc.Frame >= 0) sb.Append(",\"frame\":").Append(doc.Frame.ToString(CultureInfo.InvariantCulture));
            if (doc.TimeScale >= 0f && !float.IsInfinity(doc.TimeScale) && !float.IsNaN(doc.TimeScale))
                sb.Append(",\"time_scale\":").Append(Num(doc.TimeScale, 4, false));
            WriteLayerNames(sb, doc.LayerNames);

            sb.Append(",\"cameras\":[");
            for (var i = 0; i < doc.Cameras.Count; i++)
            {
                if (i > 0) sb.Append(',');
                WriteCamera(sb, doc.Cameras[i]);
            }

            sb.Append("],\"objects\":[");
            var limit = Math.Min(maxObjects, doc.Objects.Count);
            var budgetEnd = start + maxChars;
            var cutBySize = false;
            for (var i = 0; i < limit; i++)
            {
                if ((i & 63) == 63 && expired != null && expired()) break;
                if (sb.Length > budgetEnd) { cutBySize = true; break; }
                if (sent > 0) sb.Append(',');
                WriteObject(sb, doc.Objects[i]);
                sent++;
            }

            sb.Append(']');
            if (doc.Seen >= 0) sb.Append(",\"objects_seen\":").Append(doc.Seen.ToString(CultureInfo.InvariantCulture));
            var qualified = doc.Qualified;
            if (qualified >= 0 && qualified < sent) qualified = sent; // never fewer than were written
            if (qualified >= 0) sb.Append(",\"objects_qualified\":").Append(qualified.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"objects_sent\":").Append(sent.ToString(CultureInfo.InvariantCulture));
            // Incomplete (the read stopped before it had looked at everything): fewer objects than there are, count unknown.
            truncated = (qualified >= 0 && sent < qualified) || (doc.Incomplete && sent > 0) || (qualified < 0 && cutBySize);
            if (truncated) sb.Append(",\"truncated\":true");

            string skipped = doc.Skipped;
            if (skipped == null && sent == 0 && (truncated || doc.Incomplete))
                skipped = cutBySize ? ManagedViewSceneSnapshot.SkippedSizeBudget : ManagedViewSceneSnapshot.SkippedTimeBudget;
            if (skipped != null && sent == 0) sb.Append(",\"skipped\":").Append(JsonWire.String(skipped));
            sb.Append('}');
        }

        static void WriteLayerNames(StringBuilder sb, string[] names)
        {
            if (names == null) return;
            var any = false;
            for (var i = 0; i < names.Length && i < 32; i++)
            {
                if (string.IsNullOrEmpty(names[i])) continue;
                sb.Append(any ? "," : ",\"layer_names\":{");
                any = true;
                sb.Append('"').Append(i.ToString(CultureInfo.InvariantCulture)).Append("\":").Append(JsonWire.String(names[i]));
            }

            if (any) sb.Append('}');
        }

        static void WriteCamera(StringBuilder sb, SceneCameraDoc c)
        {
            sb.Append("{\"id\":").Append(JsonWire.String(c.Id.ToString(CultureInfo.InvariantCulture)));
            sb.Append(",\"position\":[").Append(Num(c.PX, 3)).Append(',').Append(Num(c.PY, 3)).Append(',').Append(Num(c.PZ, 3)).Append(']');
            if (!ManagedViewSceneBuilder.IsIdentity(c.QX, c.QY, c.QZ, c.QW)) Quaternion(sb, c.QX, c.QY, c.QZ, c.QW);
            if (c.Orthographic) sb.Append(",\"projection\":\"orthographic\",\"ortho_height\":").Append(Num(c.OrthoHeight, 3));
            else sb.Append(",\"projection\":\"perspective\",\"fov_y\":").Append(Num(c.FovY, 3));
            sb.Append(",\"aspect\":").Append(Num(c.Aspect, 4, false));
            sb.Append(",\"near\":").Append(Num(c.Near, 3)).Append(",\"far\":").Append(Num(c.Far, 3));
            if (!(c.VX == 0 && c.VY == 0 && c.VW == 1 && c.VH == 1))
                sb.Append(",\"viewport\":[").Append(Num(c.VX, 4, false)).Append(',').Append(Num(c.VY, 4, false)).Append(',').Append(Num(c.VW, 4, false)).Append(',').Append(Num(c.VH, 4, false)).Append(']');
            sb.Append(",\"order\":").Append(c.Order.ToString(CultureInfo.InvariantCulture));
            if (c.Main) sb.Append(",\"main\":true");
            sb.Append(",\"target\":\"").Append(c.RendersToTexture ? "texture" : "screen").Append('"');
            if (c.ProjectionMatrix != null)
            {
                sb.Append(",\"projection_matrix\":[");
                for (var i = 0; i < 16; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(Num(c.ProjectionMatrix[i], 6, false));
                }

                sb.Append(']');
            }

            sb.Append('}');
        }

        static void WriteObject(StringBuilder sb, SceneObjectDoc o)
        {
            sb.Append("{\"id\":").Append(JsonWire.String(o.Id.ToString(CultureInfo.InvariantCulture)));
            if (o.Kind != SceneObjectKind.Other) sb.Append(",\"kind\":\"").Append(KindName(o.Kind)).Append('"');
            sb.Append(",\"center\":[").Append(Num(o.CX, 3)).Append(',').Append(Num(o.CY, 3)).Append(',').Append(Num(o.CZ, 3)).Append(']');
            if (!ManagedViewSceneBuilder.IsIdentity(o.QX, o.QY, o.QZ, o.QW)) Quaternion(sb, o.QX, o.QY, o.QZ, o.QW);
            sb.Append(",\"size\":[").Append(Num(o.SX, 3)).Append(',').Append(Num(o.SY, 3)).Append(',').Append(Num(o.SZ, 3)).Append(']');
            if (o.Camera != 0) sb.Append(",\"camera\":").Append(JsonWire.String(o.Camera.ToString(CultureInfo.InvariantCulture)));
            sb.Append(",\"visible\":true");
            if (o.Layer > 0) sb.Append(",\"layer\":").Append(o.Layer.ToString(CultureInfo.InvariantCulture));
            sb.Append('}');
        }

        static void Quaternion(StringBuilder sb, double x, double y, double z, double w) =>
            sb.Append(",\"rotation\":[").Append(Num(x, 5, false)).Append(',').Append(Num(y, 5, false)).Append(',').Append(Num(z, 5, false)).Append(',').Append(Num(w, 5, false)).Append(']');

        static string KindName(SceneObjectKind kind)
        {
            switch (kind)
            {
                case SceneObjectKind.Mesh: return "mesh";
                case SceneObjectKind.Skinned: return "skinned";
                case SceneObjectKind.Sprite: return "sprite";
                case SceneObjectKind.Particles: return "particles";
                case SceneObjectKind.Line: return "line";
                case SceneObjectKind.Tilemap: return "tilemap";
                case SceneObjectKind.Ui: return "ui";
                default: return "other";
            }
        }

        static readonly string[] Formats = BuildFormats();

        static string[] BuildFormats()
        {
            var f = new string[10];
            for (var i = 0; i < f.Length; i++) f[i] = i == 0 ? "0" : "0." + new string('#', i);
            return f;
        }

        // Invariant decimal point (never the device's comma). `decimals` is the floor of places, and with `significant` a value below 1 keeps
        // five significant digits instead when that is finer (the spec's precision rule for positions and sizes), up to 9 places.
        internal static string Num(double v, int decimals, bool significant = true)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "0";
            var places = decimals;
            var a = Math.Abs(v);
            if (significant && a > 0 && a < 1)
            {
                var needed = 4 - (int)Math.Floor(Math.Log10(a));
                if (needed > places) places = needed;
            }

            if (places > 9) places = 9;
            var text = Math.Round(v, places).ToString(Formats[places], CultureInfo.InvariantCulture);
            return text == "-0" ? "0" : text;
        }
    }
}
