using System;
using UnityEngine;

namespace NightTerrors
{
    public enum Scenario
    {
        KeepGear           = 0,
        GoNaked            = 1,
        DifferentEquipment = 2,
        SwapEquipment      = 3,
    }

    public static class ScenarioSelector
    {
        public static Scenario Choose(string weightsConfig)
        {
            string[] parts = weightsConfig.Split(',');
            int[] weights = new int[4];
            for (int i = 0; i < weights.Length; i++)
            {
                if (i < parts.Length && int.TryParse(parts[i].Trim(), out int w))
                    weights[i] = Mathf.Max(0, w);
                else
                    weights[i] = 1;
            }

            int total = 0;
            foreach (int w in weights) total += w;
            if (total <= 0) return Scenario.KeepGear;

            int roll = UnityEngine.Random.Range(0, total);
            int cumulative = 0;
            for (int i = 0; i < weights.Length; i++)
            {
                cumulative += weights[i];
                if (roll < cumulative)
                    return (Scenario)i;
            }
            return Scenario.KeepGear;
        }

        // Kit definitions for DifferentEquipment scenario.
        // Each kit is an array of prefab names. Pick one themed to the situation.
        public static readonly string[][] Kits = new[]
        {
            new[] { "Club", "ShieldWood" },                        // peasant
            new[] { "Torch" },                                     // just a torch
            new[] { "FishingRod" },                                // good luck
            new[] { "Hammer" },                                    // a builder's nightmare
            new[] { "SwordBlackmetal", "ShieldBlackmetal" },       // overpowered loner
        };

        public static string[] PickKit()
        {
            return Kits[UnityEngine.Random.Range(0, Kits.Length)];
        }
    }
}
