using System.Globalization;
using System.Text.RegularExpressions;

namespace NoGaReader.Utilities;

internal sealed partial class NaturalStringComparer : IComparer<string>
{
    public static NaturalStringComparer Instance { get; } = new();

    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        var leftParts = NumberPattern().Split(left);
        var rightParts = NumberPattern().Split(right);
        var count = Math.Min(leftParts.Length, rightParts.Length);

        for (var index = 0; index < count; index++)
        {
            var leftPart = leftParts[index];
            var rightPart = rightParts[index];
            int comparison;

            if (long.TryParse(leftPart, NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber) &&
                long.TryParse(rightPart, NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber))
            {
                comparison = leftNumber.CompareTo(rightNumber);
            }
            else
            {
                comparison = StringComparer.CurrentCultureIgnoreCase.Compare(leftPart, rightPart);
            }

            if (comparison != 0)
            {
                return comparison;
            }
        }

        return leftParts.Length.CompareTo(rightParts.Length);
    }

    [GeneratedRegex("(\\d+)")]
    private static partial Regex NumberPattern();
}
