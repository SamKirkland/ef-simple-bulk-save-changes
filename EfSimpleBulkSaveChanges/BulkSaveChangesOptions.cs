namespace EfSimpleBulkSaveChanges;

public sealed class BulkSaveChangesOptions
{
    private int _batchSize = 10_000;

    public int BatchSize
    {
        get => _batchSize;
        set
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "BatchSize must be greater than zero.");
            }

            _batchSize = value;
        }
    }
}
