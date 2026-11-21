using System.Globalization;
using System.Text.RegularExpressions;

namespace MapleLite.Plugins.DpsSimulator;

internal sealed record GearStats
{
    public static readonly string[] Names =
    [
        "STR", "DEX", "INT", "LUK", "Weapon Attack", "Magic Attack", "Accuracy", "Crit Rate", "Crit Damage",
        "Attack Power", "Max HP", "Max MP", "Weapon Def", "Magic Def", "Avoidability", "Speed", "Jump",
    ];

    readonly int[] _values = new int[Names.Length];
    public int this[int i] => _values[i];

    public static bool TryParse(string text, out GearStats stats)
    {
        stats = new();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.Contains(':')) continue;
            var parts = line.Split(':', 2, StringSplitOptions.TrimEntries);
            string label = parts[0].ToUpperInvariant() switch
            {
                "HP" => "Max HP", "MP" => "Max MP", "WEAPON DEFENSE" => "Weapon Def", "MAGIC DEFENSE" => "Magic Def",
                _ => parts[0],
            };
            int i = Array.FindIndex(Names, name => name.Equals(label, StringComparison.OrdinalIgnoreCase));
            if (i < 0) continue;
            var number = Regex.Match(parts[1].Replace("−", "-"), @"^[+-]?\s*\d+");
            if (!number.Success || !int.TryParse(number.Value.Replace(" ", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                || value is < -100000 or > 100000) return false;
            stats._values[i] = value;
        }
        return true;
    }

    public static GearStats Sum(IEnumerable<GearStats> items)
    {
        var total = new GearStats();
        foreach (var gear in items)
            for (int i = 0; i < Names.Length; i++) total._values[i] += gear[i];
        return total;
    }
}

internal sealed record Monster(string Key, string Name, int Level, long Hp, int PhysicalDefense, int MagicDefense, int Avoid,
    string Weakness = "", string Source = "")
{
    public override string ToString() => $"{Name} · Lv {Level}";
}

internal enum Job { Beginner, Warrior, Bowman, Thief, Magician }
internal enum Attack { Weapon, Spell, LuckySeven }

internal sealed record CombatSettings(int Level = 1, Job Job = Job.Beginner, int Str = 4, int Dex = 4, int Int = 4, int Luk = 4,
    Attack Attack = Attack.Weapon, int Mastery = 0, int SkillPercent = 100, int Hits = 1, int CycleMs = 900,
    int PreviewCycleMs = 0, double ElementMultiplier = 1);

internal sealed record DamageEstimate(double Dps, double HitChance, double DamagePerCast, double SecondsToKill, string Weapon);

internal static class Combat
{
    // Published Classic World equations and measured weapon action multipliers:
    // https://meowdb.com/msclassic/guides/explaining-the-damage-formula
    public static DamageEstimate Estimate(CombatSettings settings, GearStats gear, int weapon, Monster target, bool preview)
    {
        var (level, job, str, dex, intel, luk, attack, mastery, skill, hits, ms, previewMs, element) = settings;
        str += gear[0]; dex += gear[1]; intel += gear[2]; luk += gear[3];
        var (weaponName, swing, stab, primary, secondary) = (weapon / 10000) switch
        {
            130 => ("1H sword", 1.8, 1.8, str, dex),
            131 or 132 => ("1H axe / blunt", 2.4, 1.2, str, dex),
            133 => ("Dagger", 1.0, 2.0, luk, str + dex),
            137 or 138 => ("Wand / staff", 1.8, 1.8, str, dex),
            140 => ("2H sword", 2.5, 2.5, str, dex),
            141 or 142 => ("2H axe / blunt", 3.0, 2.0, str, dex),
            143 => ("Spear", 1.5, 3.5, str, dex),
            144 => ("Polearm", 3.5, 1.5, str, dex),
            145 or 146 => ("Bow / crossbow", 2.5, 2.5, dex, str),
            147 => ("Claw", 2.5, 2.5, luk, str + dex),
            _ => ("Unsupported weapon", 0.0, 0.0, 0, 0),
        };
        double factor = (mastery / 10.0 + 0.1) * 0.7;
        double kind = skill / 100.0;
        double raw = 0;
        if (attack == Attack.Spell)
        {
            weaponName = "Spell";
            raw = kind * Math.Max(0, Math.Floor(intel / 2.0) + gear[5]) * (1 + intel * (1 + factor) / 250);
        }
        else if (swing > 0 && (attack != Attack.LuckySeven || weapon / 10000 == 147))
        {
            double action = attack == Attack.LuckySeven ? 3.0 : 0.6 * swing + 0.4 * stab;
            if (attack == Attack.LuckySeven) factor = 0.5;
            raw = kind * Math.Max(0, gear[4]) * (0.9 + (primary * (1 + factor) / 2 * action + secondary + 2 * gear[9]) / 100);
        }

        double common = 1.2 * (job == Job.Magician ? intel : dex) + 2 * level + 0.6 * luk;
        double accuracy = gear[6] + Math.Floor(job switch
        {
            Job.Magician => common / 5.1 + 20,
            Job.Thief => common / 4 + 15,
            Job.Bowman => common / 4.8 + 20,
            Job.Warrior => common / 2.5 + 10,
            _ => common / 2.5 + 5,
        });
        int gap = Math.Max(0, target.Level - level);
        double a = accuracy * 100 / (10 * gap + 255);
        double difference = a - target.Avoid;
        double spread = 0.15 + 0.2 / (1 + Math.Exp(difference / 12));
        double hit = target.Avoid == 0 || difference >= 25 + gap * (0.5 * level + 15) ? 1
            : a <= 0 ? 0 : Math.Clamp((1 + spread - target.Avoid / a) / (2 * spread), 0, 1);
        double scaling = gap < 10 ? 1 + gap * gap * 0.005 : 1 + gap * 0.05;
        int defense = attack == Attack.Spell ? target.MagicDefense : target.PhysicalDefense;
        double normal = raw * 100 / (100 + defense) * element / scaling;
        double critRate = Math.Clamp((5 + gear[7]) / 100.0, 0, 1);
        double critDamage = Math.Max(0, (20 + gear[8]) / 100.0);
        static double Cap(double damage) => Math.Clamp(damage, 1, 99999);
        double cast = normal <= 0 ? 0 : hits * hit * ((1 - critRate) * Cap(normal) + critRate * Cap(normal * (1 + critDamage)));
        double dps = cast * 1000 / (preview && previewMs > 0 ? previewMs : ms);
        return new(dps, hit, cast, dps > 0 ? target.Hp / dps : double.PositiveInfinity, weaponName);
    }
}
