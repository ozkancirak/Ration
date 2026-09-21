# Kalan

Kalan, Windows 11 görev çubuğunda AI kodlama sağlayıcılarının kota kullanımını, sıfırlanma zamanlarını ve yerel token/maliyet özetlerini gösteren native bir tepsi uygulamasıdır.

![Kalan tepsi ve flyout görünümü](docs/screenshots/tray-contact-sheet.png)

## Desteklenen sağlayıcılar

- Claude Code
- Codex CLI
- Antigravity
- OpenCode

Kalan, bu sağlayıcıların yerel kimlik dosyalarını salt okunur açar. Kaynak dosyalara token yenileme veya başka bir yazma işlemi yapmaz.

## Kurulum

Yayın paketinden `Kalan-win-x64-Setup.exe` dosyasını çalıştırın. Taşınabilir kullanım için `Kalan-win-x64-Portable.zip` arşivini açıp `Kalan.App.exe` dosyasını çalıştırabilirsiniz.

Paket self-contained Windows App SDK içerir; temiz makinede ayrıca Windows App SDK runtime kurulması gerekmez. Yayın imzalanmamışsa Windows SmartScreen uyarısı görülebilir; bu, runtime veya sertifika kurulumu gerektirdiği anlamına gelmez.

## Gizlilik

Kalan kendi telemetrisi veya kullanıcı kimlik verisini dışarı göndermez; kimlik dosyaları salt okunur. Kota göstergesi için gerekli istekler doğrudan ilgili sağlayıcının resmi endpoint'ine yapılır. Ham yanıtlar tanı günlüklerine yazılmadan önce redakte edilir.

## Güncelleme

Ayarlar içindeki **Güncellemeleri denetle** düğmesi GitHub Releases beslemesini kontrol eder. Kalan otomatik indirme yapmaz; yeni sürüm bulunduğunda kullanıcı karar verir.

Varsayılan besleme `https://github.com/ozkancirak/CodexBar` adresidir. Yayın deposu farklıysa `KALAN_UPDATE_REPOSITORY` ortam değişkeniyle değiştirilebilir.

## Geliştirme

```powershell
dotnet build Kalan.slnx -c Debug
dotnet test tests\Kalan.Tests\Kalan.Tests.csproj
powershell -ExecutionPolicy Bypass -File scripts\package.ps1 -Configuration Release
```

Paketleme `artifacts\release\releases` altında Setup.exe ve portable zip üretir. Tanı günlüğü `%LOCALAPPDATA%\Kalan\kalan.log` konumundadır; uygulamanın Ayarlar ekranındaki **Günlüğü aç** düğmesi bu dosyayı açar.

Sağlayıcı işaretleri Simple Icons (CC0) kaynaklarından yararlanır; teşekkürler.
