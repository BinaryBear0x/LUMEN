# LUMEN

[English](README.md) | [Türkçe](README.tr.md)

![LUMEN uygulama simgesi – animasyon](./HuePC.App/Assets/lumen-icon-animated.gif)

LUMEN, Philips Hue Bluetooth ampullerini Windows 11 bilgisayarından doğrudan BLE ile kontrol eder. Hue Bridge gerekmez.

Bu projeyi, güncel Hue mobil uygulamasının kullanımını yeterli bulmadığım ve ampulü bilgisayardan kontrol edebileceğim bir uygulama olmadığı için yaptım. LUMEN masaüstü kontrolünü; ışık profilleri, zamanlayıcılar ve bilgisayar durumuna bağlı otomasyonlarla bir araya getiriyor.

LUMEN, Philips Hue'nun resmî uygulaması değildir. BLE kontrolü test edilen ampulde çalışır; başka model ve firmware'ler ayrıca doğrulanmalıdır.

## Neler var?

- Ampul bulma, bağlanma, Windows eşleştirmesini kontrol etme ve son kullanılan ampulü hatırlama.
- Güç, parlaklık, beyaz ışık sıcaklığı, renk ve ampulü tanıma kontrolleri.
- Ampul içinde çalışan efektler ve hız ayarı; hazır ve özel renk profilleri.
- Bilgisayarın çıkış sesine göre ışığı değiştiren **Vuruş**, **Sakin** ve **Sinestezi** modları.
- Haftalık zamanlayıcılar, Pomodoro, haftalık alarm görünümü ve alarma doğru parlaklığı artıran uyanış rampası.
- Windows bildirimlerine uygulama ve renge göre kural ekleme.
- Ekran rengini ışığa yansıtma; gün ritmi, hava durumu ve deprem kayıtlarına göre ışık ayarı/uyarısı.
- Mikrofon/kamera kullanımı ile pil, şarj ve ağ değişikliklerini ışıkla belirtme.
- Bağlantı tanılaması, yerel günlükler ve sistem tepsisinde arka planda çalışma.

Hava durumu, harita ve deprem verileri internet kullanır. BLE ışık kontrolü yereldir. Bildirim, mikrofon ve kamera özellikleri Windows izinlerine bağlıdır.

## Gereksinimler

- Windows 11 x64, build 22000 veya üzeri
- Bluetooth LE destekli adaptör
- Bluetooth üzerinden kontrol edilebilen Philips Hue ampul

## Başlatma

1. Ampulü açın ve bilgisayarda Bluetooth'u etkinleştirin.
2. Gerekirse ampulü Windows **Ayarlar → Bluetooth ve cihazlar** bölümünden eşleştirin. Kontrol karakteristikleri şifreli bağlantı istiyor. Test edilen ampulde eşleştirme penceresi açılıştan sonra yaklaşık 30 dakika sürüyor.
3. Uygulamada **Ampul ara** deyip listeden ampulü seçin ve bağlanın.
4. **Kontrol**, **Efektler**, **Profiller**, **Müzik**, **Bildirimler**, **Ortam** veya **Zamanlayıcı** sayfalarından istediğiniz özelliği kullanın.

İlk Windows BLE keşfi uzun sürebilir. Cihaz önbellekte değilse bağlantı yaklaşık 30 saniye alabilir. Hedef ampul tek kontrol bağlantısına izin verebilir; Windows bağlandığında telefon Hue uygulamasının bağlantısı kesilebilir. Pencereyi kapatmak uygulamayı tepsiye küçültür. Alarm ve otomasyonlar için bilgisayarın açık, LUMEN'in çalışıyor olması gerekir.

## Kaynaktan çalıştırma

.NET 10 SDK ve Windows 11 gerekir.

```powershell
dotnet restore HuePC.sln
dotnet build HuePC.sln --configuration Release
dotnet run --project HuePC.App\HuePC.App.csproj
```

Doğrulama projesi MSTest/NUnit kullanmayan bir konsol çalıştırıcısıdır:

```powershell
dotnet run --project HuePC.Tests\HuePC.Tests.csproj --configuration Release
```

### GATT inceleme aracı

`tools/HuePC.HardwareProbe` GUI değildir; terminalden çalışan, Hue/Signify adaylarını inceleyen bir CLI aracıdır. GATT servislerini, characteristic'leri ve okunabilen değerleri listeler. Hue filtresini geçmeyen markasız cihazları taramaz. Characteristic yazımı ve Notify aboneliği yapmaz, ancak gerçek BLE bağlantısı kurar.

```powershell
dotnet run --project tools\HuePC.HardwareProbe\HuePC.HardwareProbe.csproj
dotnet run --project tools\HuePC.HardwareProbe\HuePC.HardwareProbe.csproj -- --adapter
dotnet run --project tools\HuePC.HardwareProbe\HuePC.HardwareProbe.csproj -- --device "AA:BB:CC:DD:EE:FF"
```

## BLE protokolü

Aşağıdaki özel GATT eşlemesi Philips'in yayımladığı bir Windows API'si değildir. LCA016, firmware 1.126.9 üzerinde gözlenen davranıştır; başka model ve firmware'ler için garanti vermez. Kontrol characteristic'leri eşleştirilmiş/şifreli bağlantı ister. Komutları **Write With Response** ile gönderin.

Kontrol servisi: `932C32BD-0000-47A2-835A-A8D455B859DD`

| Characteristic | Veri | İşlev |
|---|---|---|
| `932C32BD-0002-47A2-835A-A8D455B859DD` | `00` kapalı, `01` açık | Güç |
| `932C32BD-0003-47A2-835A-A8D455B859DD` | 1 bayt, `01`–`FE` | Parlaklık (1–254) |
| `932C32BD-0004-47A2-835A-A8D455B859DD` | `uint16 LE`, mired | Beyaz ışık sıcaklığı; test aralığı 154–455 mired |
| `932C32BD-0005-47A2-835A-A8D455B859DD` | `x uint16 LE` + `y uint16 LE` | CIE xy renk koordinatları |
| `932C32BD-0006-47A2-835A-A8D455B859DD` | `01` | Ampulü kısa süre yanıp söndürerek tanıma |
| `932C32BD-0007-47A2-835A-A8D455B859DD` | TLV alanları | Birleşik durum, renk/parlaklık ve efektler; durum bildirimleri bu characteristic üzerinden gelir |
| `932C32BD-1005-47A2-835A-A8D455B859DD` | TLV alanları | Enerji verildiğinde uygulanacak açılış davranışı |

`0007` verisi art arda eklenmiş `tür, uzunluk, değer` alanlarından oluşur. Bildirimler yalnız değişen alanları içerebilir; eksik alanı sıfır kabul etmeyin.

| TLV türü | Uzunluk | Değer |
|---|---:|---|
| `01` | 1 | Güç: `00` kapalı, `01` açık |
| `02` | 1 | Parlaklık: `01`–`FE` |
| `03` | 2 | Renk sıcaklığı, `uint16 LE` mired |
| `04` | 4 | `x uint16 LE`, ardından `y uint16 LE` |
| `06` | 1 | Efekt kimliği; `00` efekti durdurur |
| `08` | 1 | Efekt hızı: `01`–`FE` |

Örnek: `02 01 80 04 04 23 4C 56 7A`, parlaklığı `0x80` (128), xy rengini `x=0x4C23`, `y=0x7A56` yapar.

Efekt kimlikleri: `01` Mum, `02` Şömine, `03` Prizma, `0A` Parıltı, `0B` Opal, `0C` Kıvılcım, `0E` Deniz altı, `0F` Kozmos, `10` Güneş ışığı, `11` Büyü. Efekt ve hız aynı yazmada `06 01 <efekt> 08 01 <hız>` biçimindedir.

`1005` için test edilen açılış payload'ları:

| Davranış | Hex |
|---|---|
| Her zaman aç | `01 01 01 02 01 FE 03 02 6E 01 04 04 FF FF FF FF` |
| Son renk ve parlaklığı kullan | `01 01 01 02 01 FF 03 02 FF FF 04 04 FF FF FF FF` |
| Son durumu koru | `01 01 FF 02 01 FF 03 02 FF FF 04 04 FF FF FF FF` |

Bu yüklerde `FF`, ilgili değer için ampulün önceki durumunu kullanmasını belirtir.

### Kendi BLE uygulamanı yazmak

HuePC içinde çağrılabilir REST, socket veya eklenti API'si yoktur. Kendi istemcin ampule BLE GATT central olarak doğrudan bağlanmalıdır:

1. Hue reklam adını veya `0000FE0F-0000-1000-8000-00805F9B34FB` Signify servis UUID'sini aday filtresi olarak kullan. Company ID `0xFE0F` tek başına model doğrulamaz.
2. Ampulü Windows Bluetooth ayarlarından eşleştir.
3. Kontrol servisini ve ihtiyacın olan characteristic'leri keşfet.
4. Komutları yanıtlı yazmayla gönder. Durum takibi için `0007` characteristic'ine Notify aboneliği aç ve kısmi TLV bildirimlerini işle.
5. Farklı model ve firmware'lerde davranışı gerçek cihazda doğrula.

### Cihaz bilgisi UUID'leri

| Servis | Characteristic | İşlev |
|---|---|---|
| `0000FE0F-0000-1000-8000-00805F9B34FB` | `97FE6561-0001-4F62-86E9-B71EE2DA3D22` | Zigbee adresi; 8 bayt |
| Aynı servis | `97FE6561-0003-4F62-86E9-B71EE2DA3D22` | Ampul adı; UTF-8 |
| `0000180A-0000-1000-8000-00805F9B34FB` | `00002A29-0000-1000-8000-00805F9B34FB` | Üretici adı |
| Aynı servis | `00002A24-0000-1000-8000-00805F9B34FB` | Model numarası |
| Aynı servis | `00002A28-0000-1000-8000-00805F9B34FB` | Firmware sürümü |

## Geliştirme sırasında çözülen sorunlar

| Sorun | Çözüm |
|---|---|
| Windows, önbelleğinde olmayan eşleştirilmemiş BLE adresini açamayabiliyordu. Eski sorgu cihazı bulmadan zaman aşımına uğruyordu. | Önce hızlı adres bağlantısı deneniyor. Olmazsa eşleştirilmemiş Windows BLE uç noktaları 35 saniyeye kadar aranıyor ve adres tekrar deneniyor. İlk bağlantı uzayabilir. |
| Hue'nun özel GATT protokolünün ne yaptığı belli değildi; `Write` ve `Notify` isimleri komut anlamını göstermiyordu. | Ayrı HardwareProbe ile GATT yapısı incelendi; UUID ve payload'lar gerçek ampulde okuma, yanıtlı yazma ve bildirimlerle doğrulandı. |
| Eşleştirilmemiş bağlantıda bazı yazmalar başarılı görünse de ampul komutu uygulamıyordu. | Kontrol characteristic'lerinin bond istediği görüldü. Windows eşleştirmesi ve yanıtlı yazma zorunlu kılındı. |
| Paketsiz masaüstü uygulamasında Windows `NotificationChanged` hatası verebiliyordu. | Bildirim listesi iki saniyede bir yoklanıyor. Uygulama açıldığında zaten var olan bildirimler yeni bildirim sayılmıyor. |
| WASAPI bazı cihazlarda FFT'nin beklemediği `WAVE_FORMAT_EXTENSIBLE` ses biçimini döndürüyordu. | Ses, analizden önce standart PCM biçimine çevriliyor. |
| Ekran, müzik ve profiller sık aralıkla veya üst üste BLE komutu gönderebiliyordu. | Ekran örnekleri seyrekleştiriliyor, arayüz değerleri debounce ediliyor ve renk/parlaklık tek TLV yazmasında birleştiriliyor. |
| AFAD ve EMSC saatleri farklı biçimde veriyor; aynı deprem iki kez uyarı oluşturabiliyordu. | Saatler UTC'den yerel saate çevriliyor. Kayıtlar zaman, konum ve büyüklük yakınlığına göre tekilleştiriliyor. |

## Doğrulama durumu ve sınırlar

- Doğrudan GATT kontrolü **LCA016, firmware 1.126.9** ile doğrulandı. Başka Hue modeli ve firmware için uyumluluk varsaymayın.
- Deprem uyarısı deprem tahmini değildir; AFAD ve EMSC kayıtlarını yayımlandıktan sonra kontrol eder.
- Pil/şarj/ağ değişikliklerini canlı donanım koşullarında tetikleme, gerçek bir alarmda uyanış rampası ve yeni bir deprem kaydıyla uçtan uca alarm henüz doğrulanmadı.
- Hava durumu, harita ve deprem verileri çevrim içi servislerden alınır.
- LUMEN günlükleri BLE adresi, reklam alanı ve GATT değerleri içerebilir. Paylaşmadan önce bu alanları kontrol edip temizleyin.

## Yerel veriler

- Uygulama ayarları: `%LocalAppData%\HuePC\settings.json`
- Ampul adları: `%LocalAppData%\HuePC\device-aliases.json`
- Zamanlayıcılar: `%LocalAppData%\HuePC\schedules.json`
- Hatırlanan ampul: `%LocalAppData%\HuePC\remembered-device.json`
- Günlükler: `%LocalAppData%\HuePC\Logs`

## Proje yapısı

```text
HuePC.App             WPF arayüzü ve kullanıcı akışları
HuePC.Core            Modeller, protokol ve iş kuralları
HuePC.Bluetooth       Windows BLE keşfi, bağlantı ve GATT
HuePC.Audio           WASAPI loopback ses yakalama
HuePC.Notifications   Windows bildirim izleme
HuePC.System          Ekran, hava, deprem ve sistem olayları
HuePC.Infrastructure  JSON saklama ve yerel günlükler
HuePC.Tests           Çekirdek ve saklama doğrulamaları
tools/                Hue GATT inceleme CLI aracı
```

## Katkı ve lisans

Kod, dokümantasyon, farklı model/firmware doğrulaması ve hata raporları için [Katkı Rehberi](CONTRIBUTING.md)'ne bakın. Hata bildirirken Windows sürümünü, Bluetooth adaptörünü, ampul modeli ve firmware'ini ekleyin. Günlük veya GATT çıktısında cihaz adresi bırakmayın.

Proje [MIT Lisansı](LICENSE) ile yayımlanır. Üçüncü taraf paketler ve görseller kendi lisans koşullarına tabidir. Philips Hue ve Signify, ilgili hak sahiplerinin markalarıdır.
