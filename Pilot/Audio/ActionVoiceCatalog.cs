using System;
using System.Collections.Generic;
using System.IO;

namespace NecoArcDesktopPet.Pilot.Audio;

/// <summary>Approved pools; no duration filtering or deduplication across actions.</summary>
public static class ActionVoiceCatalog
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Pools =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["flick"] = Array.AsReadOnly(new[] { "nec00_2104_a.wav", "nec00_2104_b.wav", "nec00_2124.wav" }),
            ["pound"] = Array.AsReadOnly(new[] { "nec00_2005.wav" }),
            ["quick_punch"] = Array.AsReadOnly(new[] { "nec00_2121.wav", "nec00_3071.wav", "nec00_3072.wav" }),
            ["rotation"] = Array.AsReadOnly(new[] { "nec00_3031.wav", "nec00_3032.wav" }),
            ["caper"] = Array.AsReadOnly(new[] { "nec00_2610.wav", "nec00_2611.wav" }),
            ["angry1"] = Array.AsReadOnly(new[] { "nec00_2021.wav", "nec00_2123.wav", "nec00_2200.wav", "nec00_2207.wav", "nec00_2706.wav" }),
            ["rocket"] = Array.AsReadOnly(new[] { "nec00_3111.wav", "nec00_3112.wav", "nec00_3115.wav", "nec00_3116.wav", "nec00_3120.wav", "nec00_3121.wav" }),
            ["watch_phone"] = Array.AsReadOnly(new[] { "nec00_3057.wav", "nec00_3058.wav", "nec00_3060.wav", "nec00_3064.wav", "nec00_3065.wav", "nec00_3066.wav" }),
            ["goblet"] = Array.AsReadOnly(new[] { "nec00_2036.wav", "nec00_3083.wav" }),
            ["laugh"] = Array.AsReadOnly(new[] { "nec00_3031.wav" }),
            ["falling_down"] = Array.AsReadOnly(new[] { "nec00_2109.wav", "nec00_2115.wav" }),
            ["landing"] = Array.AsReadOnly(new[] { "nec00_2031.wav", "nec00_2033.wav" }),
            ["dragged"] = Array.AsReadOnly(new[] { "nec00_2301.wav" }),
            ["scruff"] = Array.AsReadOnly(new[] { "nec00_2204.wav" }),
            ["throw_away"] = Array.AsReadOnly(new[] { "nec00_2610.wav", "nec00_2611.wav" }),
            ["lope"] = Array.AsReadOnly(new[] { "nec00_2002.wav", "nec00_2505.wav", "nec00_2506.wav" }),
            ["trot_relaxed"] = Array.AsReadOnly(new[] { "nec00_2001.wav", "nec00_2700.wav" }),
            ["run_excited"] = Array.AsReadOnly(new[] { "nec00_2307.wav" }),
            ["sprint_panic"] = Array.AsReadOnly(new[] { "nec00_2004.wav", "nec00_2110.wav" }),
            ["stand"] = Array.AsReadOnly(new[] { "nec00_2301.wav" }),
            ["loitering"] = Array.AsReadOnly(new[] { "nec00_2003.wav", "nec00_2034.wav", "nec00_3056.wav" }),
            ["fall_down"] = Array.AsReadOnly(new[] { "nec00_2600.wav", "nec00_2601.wav", "nec00_2604.wav" }),
            ["throw_fly_left"] = Array.AsReadOnly(new[] { "nec00_2206.wav", "nec00_2602.wav", "nec00_2603.wav" }),
            ["rest"] = Array.AsReadOnly(new[] { "chr019_00_4200.wav" }),
            ["stretch"] = Array.AsReadOnly(new[] { "nec00_2301.wav", "nec00_2407.wav" }),
            ["startle_awake"] = Array.AsReadOnly(new[] { "nec00_2204.wav" }),
            ["grouchy"] = Array.AsReadOnly(new[] { "nec00_2200.wav", "nec00_2207.wav" })
        };

    public static bool TryGet(string resourcePath, out string action, out IReadOnlyList<string> voices)
    {
        action = resourcePath switch
        {
            "NecoPetMaterials/click/flick.gif" => "flick",
            "NecoPetMaterials/click/pound.gif" => "pound",
            "NecoPetMaterials/click/quick_punch.gif" => "quick_punch",
            "NecoPetMaterials/click/rotation.gif" => "rotation",
            "NecoPetMaterials/angry/caper.gif" => "caper",
            "NecoPetMaterials/angry/angry1.gif" => "angry1",
            "NecoPetMaterials/click/rocket.gif" or "NecoPetMaterials/click/rocket2.gif" => "rocket",
            "NecoPetMaterials/idle/watch_phone.gif" => "watch_phone",
            "NecoPetMaterials/idle/goblet.gif" => "goblet",
            "NecoPetMaterials/idle/laugh.gif" => "laugh",
            "NecoPetMaterials/fall/falling_down.gif" or "NecoPetMaterials/fall/falling_down2.gif" => "falling_down",
            "NecoPetMaterials/fall/landing.gif" => "landing",
            "NecoPetMaterials/drag/dragged.gif" => "dragged",
            "NecoPetMaterials/drag/scruff.gif" => "scruff",
            "NecoPetMaterials/drag/throw_away.gif" => "throw_away",
            "NecoPetMaterials/move/lope.gif" => "lope",
            "NecoPetMaterials/move/trot_relaxed.gif" => "trot_relaxed",
            "NecoPetMaterials/move/run_excited.gif" => "run_excited",
            "NecoPetMaterials/move/sprint_panic.gif" => "sprint_panic",
            "NecoPetMaterials/idle/stand.gif" => "stand",
            "NecoPetMaterials/idle/loitering.gif" => "loitering",
            "NecoPetMaterials/special/fall_down.gif" => "fall_down",
            "NecoPetMaterials/drag/throw_fly_left.gif" => "throw_fly_left",
            "NecoPetMaterials/rest/prone_prop_dangle.gif" => "rest",
            "NecoPetMaterials/sleep/stretch.gif" => "stretch",
            "NecoPetMaterials/sleep/startle_awake.gif" => "startle_awake",
            "NecoPetMaterials/sleep/grouchy.gif" => "grouchy",
            _ => ""
        };
        voices = action.Length != 0 ? Pools[action] : Array.Empty<string>();
        return voices.Count != 0;
    }

    // The startup resolver keeps its own existing env-root policy.
    public static string DevelopmentRoot => Path.Combine(AppContext.BaseDirectory, "Voice");
}
