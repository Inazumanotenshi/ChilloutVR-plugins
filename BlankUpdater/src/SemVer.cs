using System;

namespace BlankUpdater
{
    /// <summary>
    /// Compares versions like 0.3.0, 0.42.1, 1.0.0.2 and 0.3.0-beta.4.
    /// A version with a pre-release part (-beta.4) is older than the same version without it.
    /// </summary>
    internal static class SemVer
    {
        public static int Compare(string a, string b)
        {
            Split(a, out int[] numbersA, out string preA);
            Split(b, out int[] numbersB, out string preB);

            for (int i = 0; i < 4; i++)
            {
                int c = numbersA[i].CompareTo(numbersB[i]);
                if (c != 0)
                    return c;
            }

            if (preA == null && preB == null)
                return 0;
            if (preA == null)
                return 1;
            if (preB == null)
                return -1;

            string[] partsA = preA.Split('.');
            string[] partsB = preB.Split('.');
            for (int i = 0; i < Math.Min(partsA.Length, partsB.Length); i++)
            {
                bool numericA = int.TryParse(partsA[i], out int na);
                bool numericB = int.TryParse(partsB[i], out int nb);
                int c;
                if (numericA && numericB)
                    c = na.CompareTo(nb);
                else if (numericA)
                    c = -1;
                else if (numericB)
                    c = 1;
                else
                    c = string.CompareOrdinal(partsA[i].ToLowerInvariant(), partsB[i].ToLowerInvariant());
                if (c != 0)
                    return c;
            }
            return partsA.Length.CompareTo(partsB.Length);
        }

        private static void Split(string version, out int[] numbers, out string preRelease)
        {
            numbers = new int[4];
            preRelease = null;
            if (string.IsNullOrEmpty(version))
                return;

            string v = version.Trim().TrimStart('v', 'V');
            int plus = v.IndexOf('+');
            if (plus >= 0)
                v = v.Substring(0, plus);
            int dash = v.IndexOf('-');
            if (dash >= 0)
            {
                preRelease = v.Substring(dash + 1);
                v = v.Substring(0, dash);
            }

            string[] parts = v.Split('.');
            for (int i = 0; i < Math.Min(parts.Length, 4); i++)
                int.TryParse(parts[i], out numbers[i]);
        }
    }
}
