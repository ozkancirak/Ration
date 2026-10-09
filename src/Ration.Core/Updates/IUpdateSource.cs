namespace Ration.Core.Updates;

/// <summary>Kaynağın bulduğu güncelleme. <paramref name="Package"/> kaynağa özgü, dışarıya kapalı veridir.</summary>
public sealed record AvailableUpdate(string Version, object? Package = null);

/// <summary>
/// Güncelleme paketlerinin geldiği yer. Gerçek uygulama Velopack kullanır; testler sahte kaynak verir,
/// bu yüzden hiçbir test ağa veya kurulum dizinine dokunmaz.
/// </summary>
public interface IUpdateSource
{
    /// <summary>Uygulama bir kurulumdan mı çalışıyor (taşınabilir zip ve geliştirme değil).</summary>
    bool IsInstalled { get; }

    /// <summary>Yeni sürüm yoksa null.</summary>
    Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken);

    /// <summary>Paketi indirir ve doğrular; uygulamaya dokunmaz.</summary>
    Task DownloadAsync(AvailableUpdate update, CancellationToken cancellationToken);
}
