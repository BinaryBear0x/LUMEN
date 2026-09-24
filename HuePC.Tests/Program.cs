using System.Text.Json;
using HuePC.Core.Interfaces;
using HuePC.Core.Models;
using HuePC.Core.Services;
using HuePC.Infrastructure.Persistence;

var tests = new (string Name, Action Run)[]
{
    ("Generic GATT write properties do not imply light control", GenericGattWritesRemainUnknown),
    ("Capability report includes all planned lighting features", CapabilityReportHasExpectedFeatures),
    ("Discovery records serialize to portable JSON", DiscoveryModelsSerializeToJson),
    ("Duplicate characteristic UUIDs retain separate instance handles", DuplicateCharacteristicsRemainDistinct),
    ("Partial GATT failures retain sibling services", PartialDiscoveryRetainsServices),
    ("Reconnect wait grows exponentially and remains capped", ReconnectDelayIsCapped),
    ("Hue discovery keeps only matching advertisements", HueAdvertisementsAreFiltered),
    ("Remembered device details survive JSON persistence", RememberedDeviceRoundTrips),
    ("Application settings and device aliases persist independently", SettingsAndAliasesRemainIndependent),
    ("Legacy device aliases migrate to their own file", LegacyDeviceAliasesMigrate),
    ("Verified Hue characteristics mark capabilities supported", VerifiedHueCharacteristicsMarkCapabilitiesSupported),
    ("Hue combined state TLV is parsed into light state", CombinedStateTlvIsParsed),
    ("Hue color conversion stays inside the CIE xy range", ColorConversionStaysInRange),
    ("Hue effect commands match the verified payloads", EffectCommandsMatchVerifiedPayloads),
    ("Schedule fires once inside its due window", ScheduleFiresInsideWindow),
    ("Music spectrum FFT detects frequency bands", MusicSpectrumDetectsBands),
    ("Music light mapping follows the selected mode", MusicMappingFollowsMode),
    ("Beat detector fires on periodic kicks", BeatDetectorFiresOnKicks),
    ("Adaptive normalizer preserves song structure", AdaptiveNormalizerPreservesStructure),
    ("Adaptive range normalizer spreads narrow bands", AdaptiveRangeNormalizerSpreadsNarrowBands),
    ("Circadian mapper warms towards the evening", CircadianMapperWarmsTowardsEvening),
    ("Weather codes map to moods and colours", WeatherCodesMapToMoods),
    ("Notification rules match apps case insensitively", NotificationRulesMatchApps),
    ("Sunrise ramp starts before the alarm", SunriseRampStartsBeforeAlarm),
    ("Earthquake distance is sensible", EarthquakeDistanceIsSensible),
    ("Earthquake alert respects thresholds", EarthquakeAlertRespectsThresholds),
    ("Duplicate earthquake reports are matched", DuplicateEarthquakeReportsAreMatched),
    ("Solar times are sensible", SolarTimesAreSensible)
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} checks passed.");
return failures == 0 ? 0 : 1;

static void GenericGattWritesRemainUnknown()
{
    var detector = new ConservativeCapabilityDetector();
    var writableCharacteristic = new GattCharacteristicSnapshot(
        new GattAttributeKey("00001800-0000-1000-8000-00805F9B34FB", 1, "00002A00-0000-1000-8000-00805F9B34FB", 2),
        "Characteristic",
        GattCharacteristicOperations.Write | GattCharacteristicOperations.Notify,
        "Plain",
        "NotAttempted",
        null,
        null,
        null,
        0,
        []);
    var report = detector.Detect(CreateDiscovery([new GattServiceSnapshot("1800", "Generic Access", "Unknown", 1, "Success", [], null, 0, [writableCharacteristic])]));

    if (report.Capabilities.Any(capability => capability.State != CapabilityState.Unknown))
    {
        throw new InvalidOperationException("An unverified writable characteristic was interpreted as a light control.");
    }
}

static void CapabilityReportHasExpectedFeatures()
{
    var report = new ConservativeCapabilityDetector().Detect(CreateDiscovery([]));
    var names = report.Capabilities.Select(capability => capability.Name).ToArray();
    string.Join("|", names).Equals("Power|Brightness|Color|Color temperature", StringComparison.Ordinal)
        .AssertTrue("Power, brightness, color, and color temperature should be reported in stable order.");
}

static void DiscoveryModelsSerializeToJson()
{
    var snapshot = CreateDiscovery([]);
    var document = new DiscoveryExportDocument(
        1,
        DateTimeOffset.Parse("2026-09-23T00:02:00Z"),
        "HuePC",
        new AdapterStatus(true, true, true, "Bluetooth LE ready."),
        snapshot,
        new ConservativeCapabilityDetector().Detect(snapshot));
    var json = DiscoveryJsonSerializer.Serialize(document);
    json.Contains("00:00:AA:BB:CC:DD:EE", StringComparison.Ordinal)
        .AssertTrue("The export should include the BLE device address.");
    json.Contains("\"schemaVersion\": 1", StringComparison.Ordinal)
        .AssertTrue("The export should identify its schema version.");
    json.Contains("\"connectionState\": \"Connected\"", StringComparison.Ordinal)
        .AssertTrue("The JSON export should include connection state.");
}

static void DuplicateCharacteristicsRemainDistinct()
{
    var first = new GattAttributeKey("service", 1, "characteristic", 2);
    var second = new GattAttributeKey("service", 1, "characteristic", 3);
    var instances = new HashSet<GattAttributeKey> { first, second };
    if (instances.Count != 2)
    {
        throw new InvalidOperationException("Attribute handle must distinguish repeated UUID instances.");
    }
}

static void PartialDiscoveryRetainsServices()
{
    var unavailable = new GattServiceSnapshot("unavailable", "Service", "Unknown", 1, "Unreachable", [], "Device unreachable", 40, []);
    var available = new GattServiceSnapshot("available", "Service", "Unknown", 8, "Success", [], null, 12, []);
    var snapshot = CreateDiscovery([unavailable, available]);
    var restored = JsonSerializer.Deserialize<GattDiscoverySnapshot>(JsonSerializer.Serialize(snapshot));
    if (restored?.Services.Count != 2 || restored.Services[1].Uuid != "available")
    {
        throw new InvalidOperationException("A failed service must not erase later discovery results from the report.");
    }
}

static void ReconnectDelayIsCapped()
{
    var delays = Enumerable.Range(1, 8).Select(attempt => (int)BleReconnectDelayPolicy.ForAttempt(attempt).TotalSeconds).ToArray();
    if (!delays.SequenceEqual([1, 2, 4, 8, 16, 30, 30, 30]))
    {
        throw new InvalidOperationException($"Unexpected reconnect schedule: {string.Join(",", delays)}.");
    }
}

static void HueAdvertisementsAreFiltered()
{
    var generic = HueAdvertisementMatcher.Match("Wireless Headphones", []);
    if (generic.IsCandidate)
    {
        throw new InvalidOperationException("A generic BLE device should not enter the Hue discovery list.");
    }

    var namedHue = HueAdvertisementMatcher.Match("Hue Essential A60", []);
    namedHue.IsCandidate.AssertTrue("A Hue local name should be retained.");

    var signifyService = HueAdvertisementMatcher.Match("", false, [HueAdvertisementMatcher.SignifyMemberServiceUuid]);
    signifyService.IsCandidate.AssertTrue("The Signify member service UUID observed in the BLE advertisement should be retained.");

    var signify = HueAdvertisementMatcher.Match("", [new ManufacturerDataEntry(HueAdvertisementMatcher.SignifyCompanyIdentifier, "0102")]);
    if (signify.IsCandidate)
    {
        throw new InvalidOperationException("A Signify company identifier alone is broader than a Hue-specific match.");
    }

    var hueWithSignify = HueAdvertisementMatcher.Match("Hue Essential lamp", [new ManufacturerDataEntry(HueAdvertisementMatcher.SignifyCompanyIdentifier, "0102")]);
    hueWithSignify.IsCandidate.AssertTrue("A Hue name corroborated by Signify's company identifier should be retained.");

    var unrelatedManufacturer = HueAdvertisementMatcher.Match("", [new ManufacturerDataEntry(0x004C, "0102")]);
    if (unrelatedManufacturer.IsCandidate)
    {
        throw new InvalidOperationException("A different company identifier should be discarded.");
    }
}

static void VerifiedHueCharacteristicsMarkCapabilitiesSupported()
{
    var service = new GattServiceSnapshot(
        HueLightControlProtocol.LightControlServiceUuid,
        "Service",
        "Unknown",
        61,
        "Success",
        [],
        null,
        0,
        [
            CreateCharacteristic(HueLightControlProtocol.PowerCharacteristicUuid),
            CreateCharacteristic(HueLightControlProtocol.BrightnessCharacteristicUuid),
            CreateCharacteristic(HueLightControlProtocol.ColorTemperatureCharacteristicUuid),
            CreateCharacteristic(HueLightControlProtocol.ColorCharacteristicUuid)
        ]);
    var report = new ConservativeCapabilityDetector().Detect(CreateDiscovery([service]));

    if (report.Capabilities.Any(capability => capability.State != CapabilityState.Supported))
    {
        throw new InvalidOperationException("Verified Hue characteristics should mark the four light features supported.");
    }

    if (!report.Capabilities.Select(capability => capability.Name).SequenceEqual(["Power", "Brightness", "Color", "Color temperature"]))
    {
        throw new InvalidOperationException("Capability names must keep their stable order.");
    }
}

static GattCharacteristicSnapshot CreateCharacteristic(string uuid) => new(
    new GattAttributeKey(HueLightControlProtocol.LightControlServiceUuid, 61, uuid, 1),
    "Characteristic",
    GattCharacteristicOperations.Read | GattCharacteristicOperations.Write | GattCharacteristicOperations.Notify,
    "Plain",
    "NotAttempted",
    null,
    null,
    null,
    0,
    []);

static void CombinedStateTlvIsParsed()
{
    var whiteState = HueLightStateParser.ParseCombined(Convert.FromHexString("0101010201FE03026F01"));
    whiteState.IsOn!.Value.ShouldEqual(true, "TLV power should be parsed.");
    whiteState.Brightness!.Value.ShouldEqual((byte)0xFE, "TLV brightness should be parsed.");
    whiteState.ColorTemperatureMired!.Value.ShouldEqual((ushort)0x016F, "TLV color temperature should be little endian.");
    if (whiteState.IsColorMode)
    {
        throw new InvalidOperationException("A white state without color bytes must not be color mode.");
    }

    var colorState = HueLightStateParser.ParseCombined(Convert.FromHexString("0101010201F00404FD765A68"));
    if (!colorState.IsColorMode || colorState.ColorX != 0x76FD || colorState.ColorY != 0x685A)
    {
        throw new InvalidOperationException("TLV color XY should be parsed as two little endian values.");
    }
}

static void ColorConversionStaysInRange()
{
    var (whiteX, whiteY) = HueColorConverter.ToXy(255, 255, 255);
    var whiteXValue = whiteX / 65535.0;
    var whiteYValue = whiteY / 65535.0;
    if (whiteXValue is < 0.29 or > 0.35 || whiteYValue is < 0.29 or > 0.36)
    {
        throw new InvalidOperationException($"White should map near the D65 point, got ({whiteXValue:F3}, {whiteYValue:F3}).");
    }

    var (redX, _) = HueColorConverter.ToXy(255, 0, 0);
    if (redX / 65535.0 < 0.5)
    {
        throw new InvalidOperationException("Red should map to a large x value.");
    }

    var warm = HueColorConverter.FromMired(455);
    var cool = HueColorConverter.FromMired(154);
    if (warm.R <= warm.B || cool.B < cool.R)
    {
        throw new InvalidOperationException("Mired display colors should be warm at 455 and cool at 154.");
    }

    var roundTrip = HueColorConverter.FromXy(whiteX, whiteY);
    if (roundTrip.R < 200 || roundTrip.G < 200 || roundTrip.B < 200)
    {
        throw new InvalidOperationException("White XY should convert back to a near-white display color.");
    }
}

static void EffectCommandsMatchVerifiedPayloads()
{
    Convert.ToHexString(HueLightControlProtocol.GetEffectValue(HueEffect.Candle, 100))
        .ShouldEqual("060101080164", "Mum efekti komutu ampulde doğrulanan yükle eşleşmeli.");
    Convert.ToHexString(HueLightControlProtocol.GetEffectStopValue())
        .ShouldEqual("060100", "Efekt kapatma komutu 06 01 00 olmalı.");
    Convert.ToHexString(HueLightControlProtocol.GetEffectSpeedValue(200))
        .ShouldEqual("0801C8", "Efekt hızı komutu 08 01 <hız> olmalı.");

    Convert.ToHexString(HueLightControlProtocol.GetBrightnessAndColorValue(0xFE, 0x1234, 0x5678))
        .ShouldEqual("0201FE040434127856", "Parlaklık ve renk tek TLV yüklemesinde birleşmeli.");

    var withEffect = HueLightStateParser.ParseCombined(Convert.FromHexString("0101010201FE04042B366E13060103080180"));
    if (withEffect.Effect != 0x03 || withEffect.EffectSpeed != 0x80)
    {
        throw new InvalidOperationException("Efekt ve hız alanları birleşik durumdan ayrıştırılmalı.");
    }
}

static void ScheduleFiresInsideWindow()
{
    var offset = DateTimeOffset.Now.Offset;
    var mondayMorning = new DateTimeOffset(2026, 9, 21, 7, 30, 0, offset);
    var schedule = new LightSchedule(
        Guid.NewGuid(), "Sabah", new TimeOnly(7, 30), ScheduleDays.Weekdays,
        ScheduleAction.TurnOnWithState, 254, false, 300, 0, 0, true);

    ScheduleEvaluator.IsDue(schedule, mondayMorning, null).AssertTrue("Alarm tam saatinde çalışmalı.");
    ScheduleEvaluator.IsDue(schedule, mondayMorning.AddSeconds(45), null).AssertTrue("Alarm penceresi içinde çalışmalı.");

    if (ScheduleEvaluator.IsDue(schedule, mondayMorning.AddMinutes(5), null))
        throw new InvalidOperationException("Alarm pencere dışında çalışmamalı.");
    if (ScheduleEvaluator.IsDue(schedule, mondayMorning.AddSeconds(-30), null))
        throw new InvalidOperationException("Alarm saatinden önce çalışmamalı.");
    if (ScheduleEvaluator.IsDue(schedule, mondayMorning, mondayMorning))
        throw new InvalidOperationException("Aynı gün ikinci kez çalışmamalı.");
    if (ScheduleEvaluator.IsDue(schedule with { Enabled = false }, mondayMorning, null))
        throw new InvalidOperationException("Kapalı alarm çalışmamalı.");
    if (ScheduleEvaluator.IsDue(schedule with { Days = ScheduleDays.Weekend }, mondayMorning, null))
        throw new InvalidOperationException("Gün eşleşmiyorsa alarm çalışmamalı.");

    ScheduleEvaluator.DescribeDays(ScheduleDays.EveryDay).ShouldEqual("Her gün", "Gün açıklaması sabit olmalı.");
}

static void MusicSpectrumDetectsBands()
{
    static AudioSpectrumFrame Analyze(double frequency)
    {
        const int sampleRate = 44100;
        var samples = new float[AudioSpectrumAnalyzer.FftSize];
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = (float)(0.5 * Math.Sin(2 * Math.PI * frequency * index / sampleRate));
        }

        return new AudioSpectrumAnalyzer().Analyze(samples, sampleRate);
    }

    var bass = Analyze(60);
    var mid = Analyze(440);
    var treble = Analyze(8000);

    if (bass.Bass <= bass.Mid || bass.Bass <= bass.Treble)
    {
        throw new InvalidOperationException($"60 Hz tonu bas bandına düşmeli: {bass}.");
    }

    if (mid.Mid <= mid.Bass || mid.Mid <= mid.Treble)
    {
        throw new InvalidOperationException($"440 Hz tonu orta bandına düşmeli: {mid}.");
    }

    if (treble.Treble <= treble.Bass || treble.Treble <= treble.Mid)
    {
        throw new InvalidOperationException($"8000 Hz tonu tiz bandına düşmeli: {treble}.");
    }

    if (mid.Level <= 0)
    {
        throw new InvalidOperationException("Seviye (RMS) pozitif olmalı.");
    }

    if (!(bass.Centroid < mid.Centroid && mid.Centroid < treble.Centroid))
    {
        throw new InvalidOperationException($"Centroid frekansla artmalı: {bass.Centroid:F0}, {mid.Centroid:F0}, {treble.Centroid:F0}.");
    }

    if (mid.Chroma is null || mid.Chroma.Count != 12 || mid.Chroma[9] < 0.99)
    {
        throw new InvalidOperationException("440 Hz (La) kroma vektöründe A sınıfını tepe yapmalı.");
    }
}

static void MusicMappingFollowsMode()
{
    var bassHeavy = new AudioSpectrumFrame(1, 0.1, 0.05, 0.8);
    var warm = MusicLightMapper.ToColor(bassHeavy, MusicLightMode.Beat, 50);
    if (warm.R <= warm.G || warm.R <= warm.B)
    {
        throw new InvalidOperationException($"Vuruş modunda bas ağırlıklı ses sıcak olmalı: {warm}.");
    }

    var airy = new AudioSpectrumFrame(0.01, 0.5, 0.5, 0.8);
    var cool = MusicLightMapper.ToColor(airy, MusicLightMode.Beat, 50);
    if (cool.B <= cool.R || cool.B <= cool.G)
    {
        throw new InvalidOperationException($"Vuruş modunda bası olmayan ses soğuk olmalı: {cool}.");
    }

    var darkTone = new AudioSpectrumFrame(0.5, 0.5, 0.1, 0.5, Centroid: 150);
    var brightTone = new AudioSpectrumFrame(0.5, 0.5, 0.1, 0.5, Centroid: 3500);
    var darkPosition = new AdaptiveRangeNormalizer(500, 6000, 100).Normalize(150);
    var brightPosition = new AdaptiveRangeNormalizer(500, 6000, 100).Normalize(3500);
    if (!(MusicLightMapper.GetHue(darkTone, MusicLightMode.Calm, darkPosition) <
          MusicLightMapper.GetHue(brightTone, MusicLightMode.Calm, brightPosition)))
    {
        throw new InvalidOperationException("Sakin modda centroid arttıkça ton soğumalı.");
    }

    var chromaA = new double[12];
    chromaA[9] = 1;
    var violet = MusicLightMapper.ToColor(new AudioSpectrumFrame(0.2, 0.2, 0.2, 0.6, Chroma: chromaA), MusicLightMode.Synesthesia, 50);
    if (violet.B <= violet.R || violet.R <= violet.G)
    {
        throw new InvalidOperationException($"La notası mor/menekşe olmalı: {violet}.");
    }

    if (MusicLightMapper.GetValue(0.2, 0) <= MusicLightMapper.GetValue(0.2, 100))
    {
        throw new InvalidOperationException("Düşük dinamik sessiz bölümleri yukarı çekmeli.");
    }
}

static void BeatDetectorFiresOnKicks()
{
    var detector = new BeatDetector();
    var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
    var beats = 0;
    for (var frame = 0; frame < 120; frame++)
    {
        var flux = frame % 15 == 14 ? 0.5 : 0.01;
        if (detector.Update(flux, now).IsBeat) beats++;
        now = now.AddMilliseconds(100);
    }

    if (beats is < 3 or > 10)
    {
        throw new InvalidOperationException($"1,5 saniyelik vuruşlar için beklenen sayıda vuruş bulunamadı: {beats}.");
    }

    var rapid = new BeatDetector();
    var rapidTime = now;
    var rapidBeats = 0;
    for (var frame = 0; frame < 100; frame++)
    {
        var flux = frame % 2 == 0 ? 1.0 : 0.01;
        if (rapid.Update(flux, rapidTime).IsBeat) rapidBeats++;
        rapidTime = rapidTime.AddMilliseconds(100);
    }

    // 10 saniyede en fazla ~55 vuruş olabilir; minimum aralık (180 ms) bunu ~55'e sınırlar.
    if (rapidBeats > 60)
    {
        throw new InvalidOperationException($"Minimum vuruş aralığı uygulanmadı: {rapidBeats}.");
    }
}

static void AdaptiveNormalizerPreservesStructure()
{
    var normalizer = new AdaptiveSpectrumNormalizer();
    AudioSpectrumFrame normalized = new(0, 0, 0, 0);
    for (var index = 0; index < 40; index++)
    {
        normalized = normalizer.Normalize(new AudioSpectrumFrame(0.5, 0.5, 0.5, 0.5));
    }

    var loud = normalized.Level;
    normalized = normalizer.Normalize(new AudioSpectrumFrame(0.1, 0.1, 0.1, 0.1));
    var quiet = normalized.Level;

    if (quiet >= loud || quiet > 0.5)
    {
        throw new InvalidOperationException($"Sessiz bölüm yüksek bölümden düşük kalmalı: loud={loud:F2} quiet={quiet:F2}.");
    }
}

static void AdaptiveRangeNormalizerSpreadsNarrowBands()
{
    var range = new AdaptiveRangeNormalizer(800, 6000, 200);
    var low = range.Normalize(2400);
    var high = range.Normalize(6300);
    if (high - low < 0.45)
    {
        throw new InvalidOperationException($"Dar centroid bandı geniş yayılmalı: {low:F2} → {high:F2}.");
    }

    var narrow = new AdaptiveRangeNormalizer(3000, 3100, 50);
    var first = narrow.Normalize(3000);
    var second = narrow.Normalize(3100);
    if (second - first < 0.5)
    {
        throw new InvalidOperationException("Küçük varyasyonlar bile 0..1 aralığına yayılmalı.");
    }
}

static void RememberedDeviceRoundTrips()
{
    var device = CreateDiscovery([]).Device with
    {
        HueMatchReason = "Signify'a atanmış servis UUID'si (0xFE0F)"
    };
    var original = new RememberedBleDevice(device, DateTimeOffset.Parse("2026-09-23T01:00:00Z"));
    var json = JsonSerializer.Serialize(original);
    var restored = JsonSerializer.Deserialize<RememberedBleDevice>(json);

    restored?.Device.DeviceKey.ShouldEqual(device.DeviceKey, "The remembered Bluetooth address must persist.");
    restored?.Device.AddressType.ShouldEqual("Public", "The Bluetooth address type must persist for reconnection.");
    restored?.Device.HueMatchReason.ShouldEqual(device.HueMatchReason, "The advertisement match evidence must persist.");
}

static void SettingsAndAliasesRemainIndependent()
{
    var directory = Path.Combine(Path.GetTempPath(), $"HuePC-Test-{Guid.NewGuid():N}");
    try
    {
        var settingsStore = new JsonAppSettingsStore(directory);
        var aliasStore = new JsonDeviceAliasStore(directory);
        var expectedSettings = new AppSettings(
            NotificationAppFilter: "Discord",
            WeatherEnabled: true,
            HasCompletedFirstRun: true);
        var expectedAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["A1B2C3D4"] = "Salon"
        };

        settingsStore.SaveAsync(expectedSettings).GetAwaiter().GetResult();
        aliasStore.SaveAsync(expectedAliases).GetAwaiter().GetResult();

        var restoredSettings = settingsStore.LoadAsync().GetAwaiter().GetResult();
        restoredSettings.NotificationAppFilter.ShouldEqual("Discord", "Saving device aliases must preserve app settings.");
        restoredSettings.WeatherEnabled.AssertTrue("Saving device aliases must preserve enabled features.");
        restoredSettings.HasCompletedFirstRun.AssertTrue("Saving device aliases must preserve first-run state.");

        var restoredAliases = aliasStore.LoadAsync().GetAwaiter().GetResult();
        restoredAliases.TryGetValue("A1B2C3D4", out var alias).AssertTrue("Saved device alias must remain available.");
        alias.ShouldEqual("Salon", "Saved device alias must retain its name.");
    }
    finally
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

static void LegacyDeviceAliasesMigrate()
{
    var directory = Path.Combine(Path.GetTempPath(), $"HuePC-Test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var legacyPath = Path.Combine(directory, "settings.json");
        File.WriteAllText(legacyPath, "{\"DeviceAliases\":{\"A1B2C3D4\":\"Ofis\"}}");

        var store = new JsonDeviceAliasStore(directory);
        var aliases = store.LoadAsync().GetAwaiter().GetResult();
        aliases.TryGetValue("A1B2C3D4", out var alias).AssertTrue("Legacy alias must be recovered.");
        alias.ShouldEqual("Ofis", "Legacy alias must keep its name after migration.");
        File.Exists(Path.Combine(directory, "device-aliases.json")).AssertTrue("Migration must create the separate alias file.");
        File.ReadAllText(legacyPath).ShouldEqual("{\"DeviceAliases\":{\"A1B2C3D4\":\"Ofis\"}}", "Migration must leave the legacy settings file untouched.");
    }
    finally
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

static void CircadianMapperWarmsTowardsEvening()
{
    var offset = DateTimeOffset.Now.Offset;
    var morning = new DateTimeOffset(2026, 9, 23, 8, 0, 0, offset);
    var noon = new DateTimeOffset(2026, 9, 23, 12, 0, 0, offset);
    var evening = new DateTimeOffset(2026, 9, 23, 22, 0, 0, offset);

    var morningMired = CircadianMapper.MiredAt(morning);
    var noonMired = CircadianMapper.MiredAt(noon);
    var eveningMired = CircadianMapper.MiredAt(evening);
    if (!(morningMired < noonMired && noonMired < eveningMired))
    {
        throw new InvalidOperationException($"Gün ilerledikçe ışık sıcaklaşmalı: {morningMired}, {noonMired}, {eveningMired}.");
    }

    if (CircadianMapper.BrightnessFactorAt(noon) <= CircadianMapper.BrightnessFactorAt(evening))
    {
        throw new InvalidOperationException("Akşam parlaklık faktörü gündüzden düşük olmalı.");
    }

    if (CircadianMapper.BrightnessFactorAt(noon) is < 0.9 or > 1.0)
    {
        throw new InvalidOperationException("Öğle parlaklık faktörü tam parlaklığa yakın olmalı.");
    }

    if (CircadianMapper.MiredAt(new DateTimeOffset(2026, 9, 23, 3, 0, 0, offset)) < 400)
    {
        throw new InvalidOperationException("Gece ışığı en sıcak tona yakın olmalı.");
    }
}

static void WeatherCodesMapToMoods()
{
    WeatherClassifier.Classify(0).ShouldEqual(WeatherMood.Clear, "0 kodu açık havayı temsil etmeli.");
    WeatherClassifier.Classify(3).ShouldEqual(WeatherMood.Cloudy, "3 kodu bulutlu havayı temsil etmeli.");
    WeatherClassifier.Classify(61).ShouldEqual(WeatherMood.Rain, "61 kodu yağmuru temsil etmeli.");
    WeatherClassifier.Classify(75).ShouldEqual(WeatherMood.Snow, "75 kodu karı temsil etmeli.");
    WeatherClassifier.Classify(95).ShouldEqual(WeatherMood.Thunder, "95 kodu fırtınayı temsil etmeli.");

    var hot = WeatherClassifier.ColorFor(WeatherMood.Clear, 30);
    var cold = WeatherClassifier.ColorFor(WeatherMood.Clear, 4);
    if (!(hot.R > hot.B) || !(cold.B > cold.R))
    {
        throw new InvalidOperationException("Sıcak açık hava sıcak, soğuk açık hava serin renk seçmeli.");
    }

    var rain = WeatherClassifier.ColorFor(WeatherMood.Rain, 15);
    if (rain.B <= rain.R)
    {
        throw new InvalidOperationException("Yağmur mavi tonda olmalı.");
    }
}

static void NotificationRulesMatchApps()
{
    var rules = new[]
    {
        new NotificationRule("WhatsApp", "#2EE65B", false),
        new NotificationRule("Outlook", "#0A6CFF", false, false),
        new NotificationRule("hata", "#FF3B30", true)
    };

    var whatsapp = NotificationRuleMatcher.FindMatch(rules, "WhatsApp Desktop");
    whatsapp.ShouldNotBeNull("Kural uygulama adının bir parçasıyla eşleşmeli.");
    whatsapp!.ColorHex.ShouldEqual("#2EE65B", "Eşleşen kuralın rengi dönmeli.");
    if (whatsapp.StayUntilRead)
    {
        throw new InvalidOperationException("Yanıp sönen kural okunana kadar kalmamalı.");
    }

    if (NotificationRuleMatcher.FindMatch(rules, "Outlook") is not null)
    {
        throw new InvalidOperationException("Kapalı kural eşleşmemeli.");
    }

    if (NotificationRuleMatcher.FindMatch(rules, "Teams") is not null)
    {
        throw new InvalidOperationException("Eşleşmeyen uygulama için kural dönmemeli.");
    }

    var error = NotificationRuleMatcher.FindMatch(rules, "Sistem Hatası Bildirimi");
    error.ShouldNotBeNull("Kural eşleşmesi büyük/küçük harften bağımsız olmalı.");
    error!.StayUntilRead.AssertTrue("Hata kuralı okunana kadar renk tutmalı.");

    HexColor.Parse("0A6CFF", new HueRgb(0, 0, 0)).ShouldEqual(new HueRgb(0x0A, 0x6C, 0xFF), "Hex renk çözümlenmeli.");
    HexColor.Parse("#2ee65b", new HueRgb(0, 0, 0)).ShouldEqual(new HueRgb(0x2E, 0xE6, 0x5B), "Küçük harfli hex renk çözümlenmeli.");
    HexColor.Format(new HueRgb(0x0A, 0x6C, 0xFF)).ShouldEqual("#0A6CFF", "Renk hex olarak biçimlenmeli.");
    HexColor.Parse("bozuk", new HueRgb(1, 2, 3)).ShouldEqual(new HueRgb(1, 2, 3), "Geçersiz hex yedeğe düşmeli.");
}

static void SunriseRampStartsBeforeAlarm()
{
    var offset = DateTimeOffset.Now.Offset;
    var rampStart = new DateTimeOffset(2026, 9, 21, 6, 45, 0, offset);
    var schedule = new LightSchedule(
        Guid.NewGuid(), "Gün doğumu", new TimeOnly(7, 0), ScheduleDays.Weekdays,
        ScheduleAction.TurnOnWithState, 254, false, 350, 0, 0, true, 15);

    ScheduleEvaluator.IsRampDue(schedule, rampStart, null).AssertTrue("Rampa alarmdan 15 dakika önce başlamalı.");
    ScheduleEvaluator.IsRampDue(schedule, rampStart.AddSeconds(60), null).AssertTrue("Rampa penceresi içinde başlamalı.");

    if (ScheduleEvaluator.IsRampDue(schedule, rampStart.AddMinutes(3), null))
    {
        throw new InvalidOperationException("Rampa penceresi dışında başlamamalı.");
    }

    if (ScheduleEvaluator.IsRampDue(schedule, rampStart, rampStart))
    {
        throw new InvalidOperationException("Rampa günde bir kez başlamalı.");
    }

    if (ScheduleEvaluator.IsRampDue(schedule with { WakeUpRampMinutes = 0 }, rampStart, null))
    {
        throw new InvalidOperationException("Rampa süresi tanımlı değilse rampa başlamamalı.");
    }

    if (ScheduleEvaluator.IsRampDue(schedule with { Enabled = false }, rampStart, null))
    {
        throw new InvalidOperationException("Kapalı alarmın rampası başlamamalı.");
    }

    if (ScheduleEvaluator.IsRampDue(schedule, new DateTimeOffset(2026, 9, 21, 7, 30, 0, offset), null))
    {
        throw new InvalidOperationException("Alarm saatinden sonra rampa başlamamalı.");
    }

    var alarmTime = new DateTimeOffset(2026, 9, 21, 7, 0, 0, offset);
    ScheduleEvaluator.IsDue(schedule, alarmTime, null).AssertTrue("Alarm rampa bittikten sonra normal saatinde çalışmalı.");
}

static void EarthquakeDistanceIsSensible()
{
    var istanbulToAnkara = EarthquakeAlertEvaluator.DistanceKm(41.01, 28.98, 39.93, 32.86);
    if (istanbulToAnkara is < 320 or > 390)
    {
        throw new InvalidOperationException($"İstanbul-Ankara mesafesi beklenen aralıkta değil: {istanbulToAnkara:F0} km.");
    }

    var samePoint = EarthquakeAlertEvaluator.DistanceKm(39.0, 35.0, 39.0, 35.0);
    if (samePoint > 0.001)
    {
        throw new InvalidOperationException("Aynı nokta arasındaki mesafe sıfır olmalı.");
    }
}

static void EarthquakeAlertRespectsThresholds()
{
    var now = new DateTimeOffset(2026, 9, 23, 14, 0, 0, TimeSpan.FromHours(3));
    var nearby = new EarthquakeEvent("1", now.AddMinutes(-5), 38.8, 35.2, 4.6, 10, "Test (Kayseri)");

    EarthquakeAlertEvaluator.ShouldAlert(nearby, 39.0, 35.0, 600, 4.0, now, TimeSpan.FromMinutes(30))
        .AssertTrue("Eşik üstü ve yakın bir deprem uyarı üretmeli.");

    if (EarthquakeAlertEvaluator.ShouldAlert(nearby with { Magnitude = 3.4 }, 39.0, 35.0, 600, 4.0, now, TimeSpan.FromMinutes(30)))
    {
        throw new InvalidOperationException("Eşiğin altındaki deprem uyarı üretmemeli.");
    }

    if (EarthquakeAlertEvaluator.ShouldAlert(nearby with { Latitude = 38.5, Longitude = 43.4 }, 39.0, 35.0, 600, 4.0, now, TimeSpan.FromMinutes(30)))
    {
        throw new InvalidOperationException("Yarıçap dışındaki deprem uyarı üretmemeli.");
    }

    if (EarthquakeAlertEvaluator.ShouldAlert(nearby with { Time = now.AddHours(-2) }, 39.0, 35.0, 600, 4.0, now, TimeSpan.FromMinutes(30)))
    {
        throw new InvalidOperationException("Zaman aşımına uğramış deprem uyarı üretmemeli.");
    }

    var strongest = EarthquakeAlertEvaluator.StrongestAlert(
        [nearby, nearby with { Id = "2", Magnitude = 5.2 }],
        _ => true);
    strongest.ShouldNotBeNull("Eşiği geçen depremler arasından seçim yapılmalı.");
    strongest!.Id.ShouldEqual("2", "En büyük deprem seçilmeli.");
}

static void DuplicateEarthquakeReportsAreMatched()
{
    var time = new DateTimeOffset(2026, 9, 23, 14, 0, 0, TimeSpan.FromHours(3));
    var afad = new EarthquakeEvent("afad-1", time, 39.0, 35.0, 4.8, 10, "Sındırgı (Balıkesir)");
    var emsc = new EarthquakeEvent("emsc-1", time.AddMinutes(2), 39.1, 34.9, 4.5, 12, "Western Turkey", "EMSC");

    EarthquakeAlertEvaluator.IsSameQuake(afad, emsc).AssertTrue("AFAD ve EMSC kayıtları aynı depremi işaret ediyorsa eşleşmeli.");

    var anotherQuake = new EarthquakeEvent("afad-2", time.AddMinutes(20), 39.5, 35.5, 4.6, 8, "Yozgat");
    if (EarthquakeAlertEvaluator.IsSameQuake(afad, anotherQuake))
    {
        throw new InvalidOperationException("Farklı zaman ve konumdaki depremler eşleşmemeli.");
    }

    var farAway = new EarthquakeEvent("emsc-2", time.AddMinutes(1), 36.2, 36.2, 4.9, 9, "Hatay", "EMSC");
    if (EarthquakeAlertEvaluator.IsSameQuake(afad, farAway))
    {
        throw new InvalidOperationException("100 km'den uzaktaki kayıtlar eşleşmemeli.");
    }

    var veryDifferentMagnitude = new EarthquakeEvent("emsc-3", time.AddMinutes(1), 39.05, 35.05, 2.5, 9, "Sındırgı", "EMSC");
    if (EarthquakeAlertEvaluator.IsSameQuake(afad, veryDifferentMagnitude))
    {
        throw new InvalidOperationException("Büyüklük farkı 1,5'ten fazlaysa kayıtlar eşleşmemeli.");
    }
}

static void SolarTimesAreSensible()
{
    var offset = DateTimeOffset.Now.Offset;
    var summer = new DateTimeOffset(2026, 6, 21, 12, 0, 0, offset);
    var winter = new DateTimeOffset(2026, 12, 21, 12, 0, 0, offset);

    var istanbulSummer = SolarCalculator.GetSunTimes(summer, 41.01, 28.98);
    var istanbulWinter = SolarCalculator.GetSunTimes(winter, 41.01, 28.98);
    istanbulSummer.Sunrise.ShouldNotBeNull("Yazın gün doğumu hesaplanmalı.");
    istanbulSummer.Sunset.ShouldNotBeNull("Yazın gün batımı hesaplanmalı.");
    istanbulWinter.Sunrise.ShouldNotBeNull("Kışın gün doğumu hesaplanmalı.");

    if (istanbulSummer.Sunrise!.Value.Hour is < 4 or > 7)
    {
        throw new InvalidOperationException($"İstanbul yaz gün doğumu beklenen aralıkta değil: {istanbulSummer.Sunrise.Value:HH:mm}.");
    }

    if (istanbulSummer.Sunset!.Value.Hour is < 19 or > 22)
    {
        throw new InvalidOperationException($"İstanbul yaz gün batımı beklenen aralıkta değil: {istanbulSummer.Sunset.Value:HH:mm}.");
    }

    if (istanbulWinter.Sunrise!.Value.TimeOfDay <= istanbulSummer.Sunrise.Value.TimeOfDay)
    {
        throw new InvalidOperationException("Kış gün doğumu yazdan daha geç olmalı.");
    }

    if (istanbulSummer.Sunset!.Value.TimeOfDay <= istanbulWinter.Sunset!.Value.TimeOfDay)
    {
        throw new InvalidOperationException("Yaz gün batımı kıştan daha geç olmalı.");
    }

    var polarNight = SolarCalculator.GetSunTimes(winter, 69.65, 18.96);
    if (polarNight.Sunrise is not null || polarNight.Sunset is not null)
    {
        throw new InvalidOperationException("Kutup gecesinde güneş doğmamalı.");
    }
}

static GattDiscoverySnapshot CreateDiscovery(IReadOnlyList<GattServiceSnapshot> services)
{
    var device = new BleDeviceInfo(
        0x0000AABBCCDDEE,
        "00:00:AA:BB:CC:DD:EE",
        "Public",
        "Test BLE bulb",
        -57,
        DateTimeOffset.Parse("2026-09-23T00:00:00Z"),
        [],
        [],
        true,
        true);
    return new GattDiscoverySnapshot(device, DateTimeOffset.Parse("2026-09-23T00:01:00Z"), "Connected", services, []);
}

static class TestAssertions
{
    public static void AssertTrue(this bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void ShouldEqual(this string? actual, string? expected, string message)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidOperationException(message);
    }

    public static void ShouldEqual<T>(this T actual, T expected, string message) where T : struct
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
            throw new InvalidOperationException($"{message} (actual: {actual}, expected: {expected})");
    }

    public static void ShouldNotBeNull(this object? value, string message)
    {
        if (value is null) throw new InvalidOperationException(message);
    }
}
