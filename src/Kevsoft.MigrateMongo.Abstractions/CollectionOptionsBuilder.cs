using System.Linq.Expressions;
using MongoDB.Driver;

namespace Kevsoft.MigrateMongo;

/// <summary>What happens to a declared option that MongoDB can't change on an existing collection.</summary>
public enum UnchangeableOption
{
    /// <summary>The run stops before anything changes (exit code 4).</summary>
    Fail,

    /// <summary>The option is left as it is, with a warning, and the rest of the definition is still applied.</summary>
    Skip,
}

/// <summary>
/// Whether an existing collection may be converted to capped with <c>convertToCapped</c>, which holds an exclusive database
/// lock while it copies the data, keeps only the <c>_id_</c> index and loses the oldest documents if the data is bigger than
/// the declared size.
/// </summary>
public enum ConvertToCapped
{
    Allow,

    /// <summary>Fail instead. The run stops before anything changes, and <c>--allow-convert-to-capped</c> approves one collection.</summary>
    Fail,
}

/// <summary>
/// The options a collection is created with (<c>create</c>) and kept in line with (<c>collMod</c>). Only declared options are
/// compared; anything not declared is left as it is on the server.
/// </summary>
public sealed class CollectionOptionsBuilder<TDocument>
{
    internal CollectionOptionsBuilder()
    {
    }

    /// <summary>
    /// What happens to a declared option MongoDB can't change on an existing collection (e.g. a time series metaField).
    /// Overrides <c>collections.onUnchangeable</c>; <c>--skip-unchangeable</c> skips whatever this says.
    /// </summary>
    public UnchangeableOption? OnUnchangeable { get; set; }

    /// <summary>
    /// <see cref="ConvertToCapped.Fail"/> stops this collection from being converted to capped. It can only make
    /// <c>collections.onConvertToCapped</c> stricter: <see cref="ConvertToCapped.Allow"/> doesn't override a config that says
    /// <c>fail</c>.
    /// </summary>
    public ConvertToCapped? OnConvertToCapped { get; set; }

    internal DeclaredCapped? CappedOptions { get; private set; }

    internal DeclaredTimeSeries<TDocument>? TimeSeriesOptions { get; private set; }

    internal TimeSpan? TimeSeriesExpireAfter { get; private set; }

    internal DeclaredClustered? ClusteredOptions { get; private set; }

    internal Collation? DefaultCollation { get; private set; }

    internal bool? PreAndPostImages { get; private set; }

    /// <summary>A capped collection: a fixed-size collection that overwrites its oldest documents once it's full.</summary>
    /// <param name="maxSize">MongoDB's <c>size</c>, in bytes. MongoDB rounds it up to a multiple of 256.</param>
    /// <param name="maxDocuments">MongoDB's <c>max</c>: the most documents it holds, if limited.</param>
    public CollectionOptionsBuilder<TDocument> Capped(long maxSize, long? maxDocuments = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSize);
        if (maxDocuments is { } max)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max, nameof(maxDocuments));
        }

        CappedOptions = new DeclaredCapped(maxSize, maxDocuments);
        return this;
    }

    /// <summary>
    /// A time series collection. Without a <paramref name="granularity"/>, MongoDB uses <see cref="TimeSeriesGranularity.Seconds"/>.
    /// It can only be set when the collection is created.
    /// </summary>
    /// <param name="time">The field holding each measurement's date (<c>timeField</c>), rendered through the class map.</param>
    /// <param name="meta">The field identifying a series (<c>metaField</c>), if any.</param>
    /// <param name="granularity">How far apart measurements are. It can be made coarser in place, not finer.</param>
    public CollectionOptionsBuilder<TDocument> TimeSeries(
        Expression<Func<TDocument, object?>> time,
        Expression<Func<TDocument, object?>>? meta = null,
        TimeSeriesGranularity? granularity = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        return SetTimeSeries(Field(time), meta is null ? null : Field(meta), granularity, null, null);
    }

    /// <inheritdoc cref="TimeSeries(Expression{Func{TDocument, object}}, Expression{Func{TDocument, object}}?, TimeSeriesGranularity?)"/>
    public CollectionOptionsBuilder<TDocument> TimeSeries(string time, string? meta = null, TimeSeriesGranularity? granularity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(time);
        return SetTimeSeries(time, string.IsNullOrEmpty(meta) ? null : meta, granularity, null, null);
    }

    /// <summary>
    /// A time series collection with custom bucketing instead of a granularity (MongoDB 6.3+). MongoDB requires
    /// <paramref name="bucketMaxSpan"/> and <paramref name="bucketRounding"/> to be equal. It can be made larger in place, not smaller.
    /// </summary>
    public CollectionOptionsBuilder<TDocument> TimeSeries(
        Expression<Func<TDocument, object?>> time,
        Expression<Func<TDocument, object?>>? meta,
        TimeSpan bucketMaxSpan,
        TimeSpan bucketRounding)
    {
        ArgumentNullException.ThrowIfNull(time);
        return SetTimeSeries(Field(time), meta is null ? null : Field(meta), null, bucketMaxSpan, bucketRounding);
    }

    /// <inheritdoc cref="TimeSeries(Expression{Func{TDocument, object}}, Expression{Func{TDocument, object}}?, TimeSpan, TimeSpan)"/>
    public CollectionOptionsBuilder<TDocument> TimeSeries(string time, string? meta, TimeSpan bucketMaxSpan, TimeSpan bucketRounding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(time);
        return SetTimeSeries(time, string.IsNullOrEmpty(meta) ? null : meta, null, bucketMaxSpan, bucketRounding);
    }

    /// <summary>
    /// Deletes a time series collection's measurements this long after their time (<c>expireAfterSeconds</c>). Changed, added
    /// or removed in place. For a clustered collection, pass <c>expireAfter</c> to <see cref="Clustered"/> instead.
    /// </summary>
    public CollectionOptionsBuilder<TDocument> ExpireAfter(TimeSpan expireAfter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(expireAfter, TimeSpan.FromSeconds(1));
        TimeSeriesExpireAfter = expireAfter;
        return this;
    }

    /// <summary>
    /// A clustered collection (MongoDB 5.3+): documents are stored in <c>_id</c> order, in the <c>{ _id: 1 }</c> clustered
    /// index. It can only be set when the collection is created.
    /// </summary>
    /// <param name="name">The clustered index's name (MongoDB's default is <c>_id_</c>).</param>
    /// <param name="expireAfter">
    /// Makes the clustered index a TTL index (<c>expireAfterSeconds</c>), changed in place. That only works when <c>_id</c> is a
    /// date; an <c>ObjectId</c> doesn't count.
    /// </param>
    public CollectionOptionsBuilder<TDocument> Clustered(string? name = null, TimeSpan? expireAfter = null)
    {
        if (expireAfter is { } ttl)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(ttl, TimeSpan.FromSeconds(1), nameof(expireAfter));
        }

        ClusteredOptions = new DeclaredClustered(string.IsNullOrEmpty(name) ? null : name, expireAfter);
        return this;
    }

    /// <summary>
    /// The collection's default collation, which queries and indexes without their own use. It can only be set when the
    /// collection is created.
    /// </summary>
    public CollectionOptionsBuilder<TDocument> Collation(Collation collation)
    {
        ArgumentNullException.ThrowIfNull(collation);
        DefaultCollation = collation;
        return this;
    }

    /// <summary>
    /// Records change stream pre- and post-images (MongoDB 6.0+). <c>false</c> turns them off if they're on; not declaring it
    /// leaves the server's setting alone.
    /// </summary>
    public CollectionOptionsBuilder<TDocument> ChangeStreamPreAndPostImages(bool enabled = true)
    {
        PreAndPostImages = enabled;
        return this;
    }

    private CollectionOptionsBuilder<TDocument> SetTimeSeries(
        FieldDefinition<TDocument> time,
        FieldDefinition<TDocument>? meta,
        TimeSeriesGranularity? granularity,
        TimeSpan? bucketMaxSpan,
        TimeSpan? bucketRounding)
    {
        if (bucketMaxSpan is { } span)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(span, TimeSpan.FromSeconds(1), nameof(bucketMaxSpan));
            ArgumentOutOfRangeException.ThrowIfLessThan(bucketRounding!.Value, TimeSpan.FromSeconds(1), nameof(bucketRounding));
        }

        TimeSeriesOptions = new DeclaredTimeSeries<TDocument>(time, meta, granularity, bucketMaxSpan, bucketRounding);
        return this;
    }

    private static ExpressionFieldDefinition<TDocument> Field(Expression<Func<TDocument, object?>> field) => new(field);
}

internal sealed record DeclaredCapped(long MaxSize, long? MaxDocuments);

internal sealed record DeclaredTimeSeries<TDocument>(
    FieldDefinition<TDocument> Time,
    FieldDefinition<TDocument>? Meta,
    TimeSeriesGranularity? Granularity,
    TimeSpan? BucketMaxSpan,
    TimeSpan? BucketRounding);

internal sealed record DeclaredClustered(string? Name, TimeSpan? ExpireAfter);
