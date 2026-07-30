using System;
using System.Collections.Generic;
using System.Linq;

namespace Playfront.App.System;

/// <summary>A selectable time zone: the Windows key name, the label shown, and the offset it sorts by.</summary>
public sealed record TimeZoneChoice(string Id, string Label, TimeSpan Offset);

/// <summary>
/// The time zone list, taken from Windows and relabelled in English.
///
/// Why the English names are a table here instead of read from the OS: Windows stores the display
/// name already translated ("(UTC+01:00) Bruselas, Copenhague, Madrid, París"), and .NET hands over
/// that same string - CurrentUICulture does not change it. The English resources live in
/// System32\en-US\tzres.dll.mui, which only exists when the English language pack is installed, so on
/// most machines there is nothing to read. Dropping to the IANA id would give one city per zone
/// ("Europe/Paris" for the zone that contains Madrid), which makes people unable to find their own.
///
/// What is NOT in the table, on purpose: the "(UTC+01:00)" part and the set of zones. Both come from
/// Windows at runtime, so a zone Microsoft adds or moves still shows up with the right offset - it
/// just falls back to a generated label until its name is added below.
///
/// Ordering looks wrong and is not: the list sorts on the zone's BASE offset, while the label shows
/// the offset Windows advertises. Casablanca is the case that makes the difference visible - base
/// +00:00, labelled (UTC+01:00) - and it lands at the end of the +00:00 block, above Amsterdam.
/// That is where the reference puts it. Sorting on the label instead moves it, so leave this alone.
/// </summary>
public static class WindowsTimeZones
{
    // Windows time zone key name -> the city list Windows shows in English. Ids are invariant, so this
    // maps correctly whatever language the machine is in.
    private static readonly Dictionary<string, string> EnglishNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dateline Standard Time"] = "International Date Line West",
        ["UTC-11"] = "Coordinated Universal Time-11",
        ["Aleutian Standard Time"] = "Aleutian Islands",
        ["Hawaiian Standard Time"] = "Hawaii",
        ["Marquesas Standard Time"] = "Marquesas Islands",
        ["Alaskan Standard Time"] = "Alaska",
        ["UTC-09"] = "Coordinated Universal Time-09",
        ["Pacific Standard Time (Mexico)"] = "Baja California",
        ["UTC-08"] = "Coordinated Universal Time-08",
        ["Pacific Standard Time"] = "Pacific Time (US & Canada)",
        ["US Mountain Standard Time"] = "Arizona",
        ["Mountain Standard Time (Mexico)"] = "La Paz, Mazatlan",
        ["Mountain Standard Time"] = "Mountain Time (US & Canada)",
        ["Yukon Standard Time"] = "Yukon",
        ["Central America Standard Time"] = "Central America",
        ["Central Standard Time"] = "Central Time (US & Canada)",
        ["Easter Island Standard Time"] = "Easter Island",
        ["Central Standard Time (Mexico)"] = "Guadalajara, Mexico City, Monterrey",
        ["Canada Central Standard Time"] = "Saskatchewan",
        ["SA Pacific Standard Time"] = "Bogota, Lima, Quito, Rio Branco",
        ["Eastern Standard Time (Mexico)"] = "Chetumal",
        ["Eastern Standard Time"] = "Eastern Time (US & Canada)",
        ["Haiti Standard Time"] = "Haiti",
        ["Cuba Standard Time"] = "Havana",
        ["US Eastern Standard Time"] = "Indiana (East)",
        ["Turks And Caicos Standard Time"] = "Turks and Caicos",
        ["Paraguay Standard Time"] = "Asuncion",
        ["Atlantic Standard Time"] = "Atlantic Time (Canada)",
        ["Venezuela Standard Time"] = "Caracas",
        ["Central Brazilian Standard Time"] = "Cuiaba",
        ["SA Western Standard Time"] = "Georgetown, La Paz, Manaus, San Juan",
        ["Pacific SA Standard Time"] = "Santiago",
        ["Newfoundland Standard Time"] = "Newfoundland",
        ["Tocantins Standard Time"] = "Araguaina",
        ["E. South America Standard Time"] = "Brasilia",
        ["SA Eastern Standard Time"] = "Cayenne, Fortaleza",
        ["Argentina Standard Time"] = "City of Buenos Aires",
        ["Greenland Standard Time"] = "Greenland",
        ["Montevideo Standard Time"] = "Montevideo",
        ["Magallanes Standard Time"] = "Punta Arenas",
        ["Saint Pierre Standard Time"] = "Saint Pierre and Miquelon",
        ["Bahia Standard Time"] = "Salvador",
        ["UTC-02"] = "Coordinated Universal Time-02",
        ["Mid-Atlantic Standard Time"] = "Mid-Atlantic - Old",
        ["Azores Standard Time"] = "Azores",
        ["Cape Verde Standard Time"] = "Cabo Verde Is.",
        ["UTC"] = "Coordinated Universal Time",
        ["GMT Standard Time"] = "Dublin, Edinburgh, Lisbon, London",
        ["Greenwich Standard Time"] = "Monrovia, Reykjavik",
        ["Sao Tome Standard Time"] = "Sao Tome",
        ["Morocco Standard Time"] = "Casablanca",
        ["W. Europe Standard Time"] = "Amsterdam, Berlin, Bern, Rome, Stockholm, Vienna",
        ["Central Europe Standard Time"] = "Belgrade, Bratislava, Budapest, Ljubljana, Prague",
        ["Romance Standard Time"] = "Brussels, Copenhagen, Madrid, Paris",
        ["Central European Standard Time"] = "Sarajevo, Skopje, Warsaw, Zagreb",
        ["W. Central Africa Standard Time"] = "West Central Africa",
        ["Jordan Standard Time"] = "Amman",
        ["GTB Standard Time"] = "Athens, Bucharest",
        ["Middle East Standard Time"] = "Beirut",
        ["Egypt Standard Time"] = "Cairo",
        ["E. Europe Standard Time"] = "Chisinau",
        ["Syria Standard Time"] = "Damascus",
        ["West Bank Standard Time"] = "Gaza, Hebron",
        ["South Africa Standard Time"] = "Harare, Pretoria",
        ["FLE Standard Time"] = "Helsinki, Kyiv, Riga, Sofia, Tallinn, Vilnius",
        ["Israel Standard Time"] = "Jerusalem",
        ["South Sudan Standard Time"] = "Juba",
        ["Kaliningrad Standard Time"] = "Kaliningrad",
        ["Sudan Standard Time"] = "Khartoum",
        ["Libya Standard Time"] = "Tripoli",
        ["Namibia Standard Time"] = "Windhoek",
        ["Arabic Standard Time"] = "Baghdad",
        ["Turkey Standard Time"] = "Istanbul",
        ["Arab Standard Time"] = "Kuwait, Riyadh",
        ["Belarus Standard Time"] = "Minsk",
        ["Russian Standard Time"] = "Moscow, St Petersburg",
        ["E. Africa Standard Time"] = "Nairobi",
        ["Volgograd Standard Time"] = "Volgograd",
        ["Iran Standard Time"] = "Tehran",
        ["Arabian Standard Time"] = "Abu Dhabi, Muscat",
        ["Astrakhan Standard Time"] = "Astrakhan, Ulyanovsk",
        ["Azerbaijan Standard Time"] = "Baku",
        ["Russia Time Zone 3"] = "Izhevsk, Samara",
        ["Mauritius Standard Time"] = "Port Louis",
        ["Saratov Standard Time"] = "Saratov",
        ["Georgian Standard Time"] = "Tbilisi",
        ["Caucasus Standard Time"] = "Yerevan",
        ["Afghanistan Standard Time"] = "Kabul",
        ["West Asia Standard Time"] = "Ashgabat, Tashkent",
        ["Qyzylorda Standard Time"] = "Astana",
        ["Ekaterinburg Standard Time"] = "Ekaterinburg",
        ["Pakistan Standard Time"] = "Islamabad, Karachi",
        ["India Standard Time"] = "Chennai, Kolkata, Mumbai, New Delhi",
        ["Sri Lanka Standard Time"] = "Sri Jayawardenepura",
        ["Nepal Standard Time"] = "Kathmandu",
        ["Central Asia Standard Time"] = "Bishkek",
        ["Bangladesh Standard Time"] = "Dhaka",
        ["Omsk Standard Time"] = "Omsk",
        ["Myanmar Standard Time"] = "Yangon (Rangoon)",
        ["SE Asia Standard Time"] = "Bangkok, Hanoi, Jakarta",
        ["Altai Standard Time"] = "Barnaul, Gorno-Altaysk",
        ["W. Mongolia Standard Time"] = "Hovd",
        ["North Asia Standard Time"] = "Krasnoyarsk",
        ["N. Central Asia Standard Time"] = "Novosibirsk",
        ["Tomsk Standard Time"] = "Tomsk",
        ["China Standard Time"] = "Beijing, Chongqing, Hong Kong, Urumqi",
        ["North Asia East Standard Time"] = "Irkutsk",
        ["Singapore Standard Time"] = "Kuala Lumpur, Singapore",
        ["W. Australia Standard Time"] = "Perth",
        ["Taipei Standard Time"] = "Taipei",
        ["Ulaanbaatar Standard Time"] = "Ulaanbaatar",
        ["Aus Central W. Standard Time"] = "Eucla",
        ["Transbaikal Standard Time"] = "Chita",
        ["Tokyo Standard Time"] = "Osaka, Sapporo, Tokyo",
        ["North Korea Standard Time"] = "Pyongyang",
        ["Korea Standard Time"] = "Seoul",
        ["Yakutsk Standard Time"] = "Yakutsk",
        ["Cen. Australia Standard Time"] = "Adelaide",
        ["AUS Central Standard Time"] = "Darwin",
        ["E. Australia Standard Time"] = "Brisbane",
        ["AUS Eastern Standard Time"] = "Canberra, Melbourne, Sydney",
        ["West Pacific Standard Time"] = "Guam, Port Moresby",
        ["Tasmania Standard Time"] = "Hobart",
        ["Vladivostok Standard Time"] = "Vladivostok",
        ["Lord Howe Standard Time"] = "Lord Howe Island",
        ["Bougainville Standard Time"] = "Bougainville Island",
        ["Russia Time Zone 10"] = "Chokurdakh",
        ["Magadan Standard Time"] = "Magadan",
        ["Norfolk Standard Time"] = "Norfolk Island",
        ["Sakhalin Standard Time"] = "Sakhalin",
        ["Central Pacific Standard Time"] = "Solomon Is., New Caledonia",
        ["Russia Time Zone 11"] = "Anadyr, Petropavlovsk-Kamchatsky",
        ["New Zealand Standard Time"] = "Auckland, Wellington",
        ["Fiji Standard Time"] = "Fiji",
        ["Kamchatka Standard Time"] = "Petropavlovsk-Kamchatsky - Old",
        ["UTC+12"] = "Coordinated Universal Time+12",
        ["Chatham Islands Standard Time"] = "Chatham Islands",
        ["UTC+13"] = "Coordinated Universal Time+13",
        ["Tonga Standard Time"] = "Nuku'alofa",
        ["Samoa Standard Time"] = "Samoa",
        ["Line Islands Standard Time"] = "Kiritimati Island",
    };

    private static IReadOnlyList<TimeZoneChoice>? _cache;

    /// <summary>
    /// Every zone Windows knows about, ordered west to east the way Windows orders them. Built once:
    /// the set only changes when Windows is updated, and this feeds a list the user scrolls.
    /// </summary>
    public static IReadOnlyList<TimeZoneChoice> All => _cache ??= Build();

    /// <summary>Label for one zone id, or the id itself if Windows no longer knows it.</summary>
    public static string LabelFor(string id) =>
        All.FirstOrDefault(z => string.Equals(z.Id, id, StringComparison.OrdinalIgnoreCase))?.Label ?? id;

    private static IReadOnlyList<TimeZoneChoice> Build()
    {
        var list = new List<TimeZoneChoice>();

        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            // The display name is translated, but its "(UTC+01:00)" head is not - it is the same
            // string in every language, which is exactly the part being reused here.
            var display = zone.DisplayName ?? string.Empty;
            var close = display.IndexOf(')');
            var stamp = close > 0 ? display[..(close + 1)] : Stamp(zone.BaseUtcOffset);

            string cities;
            if (EnglishNames.TryGetValue(zone.Id, out var known))
            {
                cities = known;
            }
            else
            {
                // A zone added after this table was written. Fall back to the IANA city, which is
                // invariant English, so the list stays usable instead of showing Spanish in the
                // middle of an English screen.
                cities = IanaCity(zone) ?? zone.Id;
            }

            list.Add(new TimeZoneChoice(zone.Id, $"{stamp} {cities}", zone.BaseUtcOffset));
        }

        list.Sort((a, b) =>
        {
            var byOffset = a.Offset.CompareTo(b.Offset);
            return byOffset != 0 ? byOffset : string.Compare(a.Label, b.Label, StringComparison.Ordinal);
        });

        return list;
    }

    private static string? IanaCity(TimeZoneInfo zone)
    {
        if (!TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) || iana is null)
        {
            return null;
        }

        var slash = iana.LastIndexOf('/');
        return (slash >= 0 ? iana[(slash + 1)..] : iana).Replace('_', ' ');
    }

    /// <summary>"(UTC+01:00)" for an offset. Only used when a zone has no display name at all.</summary>
    private static string Stamp(TimeSpan offset)
    {
        if (offset == TimeSpan.Zero)
        {
            return "(UTC)";
        }

        var sign = offset < TimeSpan.Zero ? "-" : "+";
        return $"(UTC{sign}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes):00})";
    }
}
