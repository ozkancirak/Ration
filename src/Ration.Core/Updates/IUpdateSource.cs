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

    /// <summary>Paketi indirir ve doğrular; uygulamaya dokunmaz. <paramref name="progress"/> yüzde bildirir.</summary>
    Task DownloadAsync(AvailableUpdate update, Action<int> progress, CancellationToken cancellationToken);

    /// <summary>
    /// İndirilmiş paketi, bu süreç çıkınca uygulanacak ve uygulama yeniden açılacak şekilde
    /// zamanlar. Süreci kendisi kapatmaz; çıkışı çağıran yapar ki tepsi simgesi düzgün temizlensin.
    /// </summary>
    void ApplyOnExitAndRestart(AvailableUpdate update);
}
