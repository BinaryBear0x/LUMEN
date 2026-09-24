# Katkı Rehberi

LUMEN'e kod, dokümantasyon, hata raporu, erişilebilirlik ve arayüz iyileştirmesi ya da farklı Hue model/firmware doğrulamasıyla katkı sunabilirsiniz. Yeni bir katkı başlatmadan önce depodaki açık issue'lara bakın; büyük değişiklikleri önce bir issue üzerinden konuşmak, aynı işin tekrarlanmasını önler.

## Hata bildirimi ve fikir önerisi

- Hata için GitHub'da **Hata bildirimi**, yeni özellik için **Özellik önerisi** şablonunu kullanın.
- Hatanın tekrarlanma adımlarını ve beklenen/gerçekleşen sonucu yazın.
- BLE veya günlük çıktısı eklemeden önce Bluetooth adresini, Zigbee adresini, kullanıcı adını ve kişisel bilgileri silin. Paylaştığınız kayıtların içeriğini kontrol edin.
- Ampulle ilgili bir hata bildiriyorsanız model, firmware, Windows sürümü ve Bluetooth adaptörünü belirtin.
- Yeni protokol davranışı veya payload iddiasını mümkünse gerçek cihazda hangi model/firmware ile doğruladığınızı açıklayın.

## Geliştirme ortamı

- Windows 11 x64 (build 22000 veya üzeri)
- .NET 10 SDK
- Uygulamayı ve BLE özelliklerini donanımda denemek için Bluetooth LE adaptörü ve desteklenen Hue Bluetooth ampulü

Depoyu fork edip kendi klonunuzda bir dal açın:

```powershell
git clone <fork-adresiniz>
cd <depo-dizini>
git switch -c <degisiklik-kisa-adi>
dotnet restore HuePC.sln
```

## Değişiklikleri derleme ve doğrulama

```powershell
dotnet build HuePC.sln --configuration Release
dotnet run --project HuePC.Tests\HuePC.Tests.csproj --configuration Release
```

Doğrulama çalıştırıcısı şu anda özel bir konsol uygulamasıdır; MSTest/NUnit kullanmaz. Bu nedenle `dotnet test` yerine yukarıdaki `dotnet run` komutunu çalıştırın. Testleri değiştirdiyseniz veya yeni test eklediyseniz konsol çıktısındaki tüm kontrollerin geçtiğini inceleyin.

Uygulamayı yerel olarak başlatmak için:

```powershell
dotnet run --project HuePC.App\HuePC.App.csproj
```

Donanım gerektiren bir davranışı test ettiyseniz ampul modeli ve firmware sürümünü PR açıklamasına ekleyin. Donanım testi yapamadıysanız bunu açıkça belirtin; doğrulanmamış davranışı doğrulanmış gibi sunmayın.

## Pull request hazırlama

- Her PR'ı tek bir konuya odaklı ve mümkün olduğunca küçük tutun.
- Mevcut proje yapısı, nullable kullanımı ve adlandırma biçimiyle tutarlı kod yazın.
- Kullanıcıya görünen davranış veya geliştirici/protokol belgeleri değiştiyse README ve ilgili dokümanları güncelleyin.
- Uygun regresyon kontrolü ekleyin veya mevcut kontrolleri güncelleyin.
- PR açıklamasında neyin değiştiğini, neden gerektiğini, çalıştırdığınız build/doğrulama komutlarını ve kalan sınırlamaları belirtin.
- Ekran görüntüsü ya da günlük eklerken kişisel bilgileri ve cihaz adreslerini temizleyin.

## BLE protokolü ve güvenli paylaşım

Projedeki özel Hue GATT UUID/payload eşlemeleri README'de belirtilen cihaz ve firmware üzerinde yapılan gözlemlere dayanır; her modele genellenemez. Protokol değişikliği içeren katkıda kullanılan UUID'yi, yazma türünü, payload'ı (cihaz kimliği içermeyecek şekilde), gözlenen sonucu ve doğrulanan model/firmware'i belgeleyin. Ampul erişim bilgileri veya kullanıcı verileri içerebilecek günlükleri olduğu gibi eklemeyin.

Katkı göndererek değişikliğinizin [MIT Lisansı](LICENSE) koşulları altında dağıtılmasını kabul etmiş olursunuz. Üçüncü taraf varlıklarının lisansları kendi koşullarına tabidir.
