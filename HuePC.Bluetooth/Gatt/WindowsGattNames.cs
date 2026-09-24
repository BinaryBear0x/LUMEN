namespace HuePC.Bluetooth.Gatt;

internal static class WindowsGattNames
{
    private static readonly IReadOnlyDictionary<int, string> ServiceNames = new Dictionary<int, string>
    {
        [0x1800] = "Generic Access",
        [0x1801] = "Generic Attribute",
        [0x180A] = "Device Information",
        [0x180F] = "Battery Service",
        [0x180D] = "Heart Rate",
        [0x1812] = "Human Interface Device"
    };

    private static readonly IReadOnlyDictionary<int, string> CharacteristicNames = new Dictionary<int, string>
    {
        [0x2A00] = "Device Name",
        [0x2A01] = "Appearance",
        [0x2A04] = "Peripheral Preferred Connection Parameters",
        [0x2A05] = "Service Changed",
        [0x2A19] = "Battery Level",
        [0x2A24] = "Model Number String",
        [0x2A25] = "Serial Number String",
        [0x2A26] = "Firmware Revision String",
        [0x2A27] = "Hardware Revision String",
        [0x2A29] = "Manufacturer Name String"
    };

    private static readonly IReadOnlyDictionary<int, string> DescriptorNames = new Dictionary<int, string>
    {
        [0x2900] = "Characteristic Extended Properties",
        [0x2901] = "Characteristic User Description",
        [0x2902] = "Client Characteristic Configuration",
        [0x2904] = "Characteristic Presentation Format",
        [0x2905] = "Characteristic Aggregate Format"
    };

    public static string Service(string uuid) => Lookup(uuid, ServiceNames, "Service");
    public static string Characteristic(string uuid) => Lookup(uuid, CharacteristicNames, "Characteristic");
    public static string Descriptor(string uuid) => Lookup(uuid, DescriptorNames, "Descriptor");

    private static string Lookup(string uuid, IReadOnlyDictionary<int, string> names, string fallback)
    {
        if (Guid.TryParse(uuid, out var guid))
        {
            var bytes = guid.ToByteArray();
            if (bytes[2] == 0 && bytes[3] == 0 &&
                bytes[4] == 0 && bytes[5] == 0 && bytes[6] == 0x10 && bytes[7] == 0 &&
                bytes[8] == 0x80 && bytes[9] == 0 && bytes[10] == 0 && bytes[11] == 0x80 &&
                bytes[12] == 0x5F && bytes[13] == 0x9B && bytes[14] == 0x34 && bytes[15] == 0xFB)
            {
                var shortUuid = (bytes[1] << 8) | bytes[0];
                if (names.TryGetValue(shortUuid, out var name))
                {
                    return name;
                }
            }
        }

        return fallback;
    }
}
