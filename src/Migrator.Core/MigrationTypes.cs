namespace Migrator.Core;

/// <summary>Bir kolonun taşıma için gereken bilgisi.</summary>
public sealed record ColumnInfo(
    string Name,
    string StoreType,
    bool IsNullable,
    bool IsIdentity,
    bool HasDefault,
    int? MaxLength);

/// <summary>Tek bir tablonun kopyalama planı.</summary>
public sealed record TablePlan(
    string Table,
    List<(ColumnInfo Source, ColumnInfo Target)> CopyColumns,
    List<ColumnInfo> SynthesizedColumns);

public sealed record ForeignKey(
    string Name,
    string ChildTable,
    string ParentTable,
    List<(string Child, string Parent)> Columns);

public enum ProgressKind { Info, Warning, Error, Success, Step }

public sealed record ProgressMessage(ProgressKind Kind, string Text);

/// <summary>Taşımanın bilinçli olarak gevşetilebilen kapıları. Hiçbiri varsayılan açık değildir.</summary>
public sealed class MigrationOptions
{
    /// <summary>Kaynakta olup hedefte olmayan tabloları yok sayar (verileri taşınmaz).</summary>
    public bool AllowSourceOnlyTables { get; init; }

    /// <summary>Ön kontrolün bulduğu NULL/uzunluk uyumsuzluklarına rağmen devam eder.</summary>
    public bool AllowSchemaRisk { get; init; }

    /// <summary>Hedef collation beklenenden farklıysa devam eder.</summary>
    public bool AllowCollationMismatch { get; init; }

    /// <summary>Yalnız doğrulama yapar, veri taşımaz.</summary>
    public bool VerifyOnly { get; init; }

    /// <summary>Beklenen ICU collation; boşsa collation kontrolü yapılmaz.</summary>
    public string? ExpectedIcuLocale { get; init; }
}

public sealed record MigrationResult(bool Succeeded, long RowsCopied, TimeSpan Duration, string Summary);
