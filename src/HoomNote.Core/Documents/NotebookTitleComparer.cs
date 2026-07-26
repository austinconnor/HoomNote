namespace HoomNote.Core.Documents;

public sealed class NotebookTitleComparer : IComparer<string?>
{
    public static NotebookTitleComparer Instance { get; } = new();

    private NotebookTitleComparer() { }

    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return 1;
        if (right is null) return -1;

        var category = Category(left).CompareTo(Category(right));
        if (category != 0) return category;

        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            var leftIsDigit = char.IsDigit(left[leftIndex]);
            var rightIsDigit = char.IsDigit(right[rightIndex]);
            if (leftIsDigit && rightIsDigit)
            {
                var numeric = CompareNumberRuns(left, ref leftIndex, right, ref rightIndex);
                if (numeric != 0) return numeric;
                continue;
            }
            if (leftIsDigit != rightIsDigit) return leftIsDigit ? -1 : 1;

            var leftCharacter = char.ToUpperInvariant(left[leftIndex]);
            var rightCharacter = char.ToUpperInvariant(right[rightIndex]);
            var character = leftCharacter.CompareTo(rightCharacter);
            if (character != 0) return character;
            leftIndex++;
            rightIndex++;
        }

        var length = (left.Length - leftIndex).CompareTo(right.Length - rightIndex);
        return length != 0 ? length : StringComparer.Ordinal.Compare(left, right);
    }

    private static int Category(string value)
    {
        if (value.Length == 0) return 2;
        if (char.IsDigit(value[0])) return 0;
        if (char.IsLetter(value[0])) return 1;
        return 2;
    }

    private static int CompareNumberRuns(
        string left,
        ref int leftIndex,
        string right,
        ref int rightIndex)
    {
        var leftStart = leftIndex;
        var rightStart = rightIndex;
        while (leftIndex < left.Length && char.IsDigit(left[leftIndex])) leftIndex++;
        while (rightIndex < right.Length && char.IsDigit(right[rightIndex])) rightIndex++;

        var leftSignificant = leftStart;
        var rightSignificant = rightStart;
        while (leftSignificant < leftIndex - 1 && left[leftSignificant] == '0') leftSignificant++;
        while (rightSignificant < rightIndex - 1 && right[rightSignificant] == '0') rightSignificant++;

        var significantLength = (leftIndex - leftSignificant)
            .CompareTo(rightIndex - rightSignificant);
        if (significantLength != 0) return significantLength;
        for (var index = 0; index < leftIndex - leftSignificant; index++)
        {
            var digit = left[leftSignificant + index].CompareTo(right[rightSignificant + index]);
            if (digit != 0) return digit;
        }

        // Equivalent numeric values use fewer leading zeroes first.
        return (leftIndex - leftStart).CompareTo(rightIndex - rightStart);
    }
}
