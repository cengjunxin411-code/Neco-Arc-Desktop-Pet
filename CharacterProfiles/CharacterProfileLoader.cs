using System;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.IO;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using NecoArcDesktopPet.Animation;

namespace NecoArcDesktopPet.CharacterProfiles;

internal static class CharacterProfileLoader
{
    // Input complexity/representation limits, not configurable safety thresholds.
    internal const int MaximumBytes = 1024 * 1024;
    private static readonly Regex ResourceKey = new(@"\ANecoPetMaterials/(?:[A-Za-z0-9_-]+/)*[A-Za-z0-9_-]+\.gif\z", RegexOptions.CultureInvariant);

    public static CharacterProfileLoadResult LoadFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Failure("Missing", "No Character Profile selected.");
        try
        {
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
                path.StartsWith("//", StringComparison.Ordinal) || path.IndexOf(':', 2) >= 0)
                return Failure("InvalidPath", "Character Profile requires an absolute local file path.");
            var full = Path.GetFullPath(path);
            if (new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network)
                return Failure("InvalidPath", "Character Profile must be on a local drive.");
            using var admitted = CharacterProfileFile.Open(full);
            var stream = admitted.Stream;
            if (stream.Length > MaximumBytes) return Failure("InvalidProfile", "Character Profile exceeds the input limit.");
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, MaximumBytes + 1 - bytes.Length))) != 0)
            {
                bytes.Write(buffer, 0, count);
                if (bytes.Length > MaximumBytes) return Failure("InvalidProfile", "Character Profile exceeds the input limit.");
            }
            bytes.Position = 0;
            using var reader = new StreamReader(bytes, new UTF8Encoding(false, true), true);
            return Parse(reader.ReadToEnd());
        }
        catch (FileNotFoundException) { return Failure("Missing", "Character Profile file is missing."); }
        catch (DirectoryNotFoundException) { return Failure("Missing", "Character Profile directory is missing."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException)
        { return Failure("ReadFailed", "Character Profile could not be read as a local UTF-8 file."); }
    }

    public static CharacterProfileLoadResult Parse(string json)
    {
        if (json == null || Encoding.UTF8.GetByteCount(json) > MaximumBytes)
            return Failure("InvalidProfile", "Character Profile exceeds the input limit.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
            var root = document.RootElement;
            Object(root, "profile", "schemaVersion", "id", "canonicalCanvas", "logicalAspect", "animations");
            var version = Required(root, "schemaVersion");
            if (!version.TryGetInt32(out var schema) || schema != 1)
                return Failure("UnsupportedVersion", "Only Character Profile schemaVersion 1 is supported.");
            var idNode = Required(root, "id");
            if (idNode.ValueKind != JsonValueKind.String || idNode.GetString() is not { Length: > 0 and <= 80 } id || string.IsNullOrWhiteSpace(id))
                throw Invalid("id must be a nonempty string of at most 80 characters.");
            var canvas = Canvas(Required(root, "canonicalCanvas"), "canonicalCanvas");
            var aspect = Number(Required(root, "logicalAspect"), "logicalAspect", 0.01, 100);
            var entries = Required(root, "animations");
            if (entries.ValueKind != JsonValueKind.Object) throw Invalid("animations must be an object.");
            var animations = new Dictionary<string, AnimationCalibration>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in entries.EnumerateObject())
            {
                if (animations.Count >= 256 || !ResourceKey.IsMatch(property.Name) || animations.ContainsKey(property.Name))
                    throw Invalid("animations contains an invalid, duplicate or excessive resource key.");
                var value = property.Value;
                Object(value, property.Name, "canvas", "visual", "contacts", "detectionBounds", "sideOverlay", "headAnchor", "bodyAnchor");
                var size = Canvas(Required(value, "canvas"), "canvas");
                var visual = Required(value, "visual");
                Object(visual, "visual", "scale", "offsetXFactor", "offsetYFactor");
                var correction = new AnimationVisualProfile(
                    Number(Required(visual, "scale"), "scale", 0.001, 100),
                    Number(Required(visual, "offsetXFactor"), "offsetXFactor", -100, 100),
                    Number(Required(visual, "offsetYFactor"), "offsetYFactor", -100, 100));
                var contacts = default(ProfileContacts);
                if (value.TryGetProperty("contacts", out var contact))
                {
                    Object(contact, "contacts", "top", "left", "right");
                    contacts = new(OptionalPoint(contact, "top"), OptionalPoint(contact, "left"), OptionalPoint(contact, "right"));
                }
                Rect? bounds = null;
                if (value.TryGetProperty("detectionBounds", out var rect))
                {
                    Object(rect, "detectionBounds", "x", "y", "width", "height");
                    var x = Number(Required(rect, "x"), "x", 0, size.Width);
                    var y = Number(Required(rect, "y"), "y", 0, size.Height);
                    var w = Number(Required(rect, "width"), "width", 0.001, size.Width);
                    var h = Number(Required(rect, "height"), "height", 0.001, size.Height);
                    if (x + w > size.Width || y + h > size.Height) throw Invalid("detectionBounds must stay inside canvas.");
                    bounds = new Rect(x, y, w, h);
                }
                SideOverlayCalibration? overlay = null;
                if (value.TryGetProperty("sideOverlay", out var side))
                {
                    Object(side, "sideOverlay", "wallInset", "bottomToContact");
                    overlay = new(
                        Number(Required(side, "wallInset"), "wallInset", 0, size.Width / 2),
                        Number(Required(side, "bottomToContact"), "bottomToContact", 0, size.Height));
                }
                animations.Add(property.Name, new(size, correction, contacts, bounds, overlay,
                    OptionalPoint(value, "headAnchor"), OptionalPoint(value, "bodyAnchor")));
            }
            if (animations.Count == 0) throw Invalid("animations must contain at least one explicit calibration.");
            return new(new(id, canvas, aspect, animations.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase)), "Valid", "Character Profile validated; restart is required to change it.");
        }
        catch (JsonException) { return Failure("InvalidJson", "Character Profile JSON is invalid."); }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException or FormatException or OverflowException)
        { return Failure("InvalidProfile", e is InvalidDataException ? e.Message : "Character Profile has invalid field types or numbers."); }
    }

    private static void Object(JsonElement value, string name, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid(name + " must be an object.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in value.EnumerateObject())
            if (!seen.Add(field.Name) || Array.IndexOf(fields, field.Name) < 0)
                throw Invalid(name + " contains an unknown or duplicate field.");
    }
    private static JsonElement Required(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) ? value : throw Invalid("Missing required field: " + name);
    private static double Number(JsonElement value, string name, double minimum, double maximum)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) ||
            !double.IsFinite(number) || number < minimum || number > maximum)
            throw Invalid(name + " must be a finite number in the documented range.");
        return number;
    }
    private static Size Canvas(JsonElement value, string name)
    {
        Object(value, name, "width", "height");
        var width = Number(Required(value, "width"), "width", 1, 16384);
        var height = Number(Required(value, "height"), "height", 1, 16384);
        if (width != Math.Truncate(width) || height != Math.Truncate(height)) throw Invalid(name + " requires integer pixel dimensions.");
        return new(width, height);
    }
    private static Point? OptionalPoint(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var point)) return null;
        Object(point, name, "x", "y");
        return new Point(Number(Required(point, "x"), name + ".x", 0, 1), Number(Required(point, "y"), name + ".y", 0, 1));
    }
    private static InvalidDataException Invalid(string message) => new(message);
    private static CharacterProfileLoadResult Failure(string code, string message) => new(null, code, message);
}
