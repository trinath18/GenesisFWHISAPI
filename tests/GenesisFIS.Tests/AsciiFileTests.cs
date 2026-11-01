using GenesisFIS.Core.Cases;

namespace GenesisFIS.Tests;

public class AsciiFileTests
{
    private static AsciiRow Row(params (string Key, object? Value)[] values) => new(values.ToDictionary(v => v.Key, v => v.Value));

    [Fact]
    public void L_pads_and_cuts_like_the_legacy_LSet()
    {
        Assert.Equal("AB   ", FixedWidth.L("AB", 5));
        Assert.Equal("ABCDE", FixedWidth.L("ABCDEFG", 5));
        Assert.Equal("   ", FixedWidth.L(null, 3));
        Assert.Equal("", FixedWidth.L("X", 0));
    }

    [Fact]
    public void Dates_amounts_and_numbers_use_the_legacy_formats()
    {
        Assert.Equal("05032024", FixedWidth.Dmy(new DateTime(2024, 3, 5)));
        Assert.Equal("", FixedWidth.Dmy(null));
        Assert.Equal("", FixedWidth.Dmy(new DateTime(1900, 1, 1)));
        Assert.Equal("1,234.50", FixedWidth.Money(1234.5m));
        Assert.Equal("0.00", FixedWidth.Money(0m));
        Assert.Equal("000042", FixedWidth.Zero(42, 6));
        var r = Row(("Amt", 5000.0000m), ("Half", 12.5000m), ("Code", " AB "), ("Nothing", DBNull.Value));
        Assert.Equal("5000", r.S("Amt"));      // VB CStr of a money column
        Assert.Equal("12.5", r.S("Half"));
        Assert.Equal("AB", r.S("code"));       // column names are case-insensitive, values trimmed
        Assert.Equal("", r.S("Nothing")); Assert.False(r.Has("Missing"));
    }

    [Fact]
    public void Claim_file_header_trailer_and_record_keep_the_legacy_layout()
    {
        Assert.Equal("FWM00000331102026\r\n", ClaimAsciiLayout.Header("WM", "000003", new DateTime(2026, 10, 31)));
        var trailer = ClaimAsciiLayout.Trailer(12);
        Assert.Equal("T12        " + new string(' ', 164) + "\r\n", trailer);

        var row = Row(("CLMBordxWSStatus", "A"), ("BordxRefNo", "B001"), ("BordxDate", new DateTime(2024, 1, 2)), ("PAYCode", "WM"), ("CasNumber", "WM24000001"), ("ClaimVersion", 2),
            ("MBMNumber", "WM0001*01"), ("MBMPatientCovID", "00"), ("MBMPolicyNo", "P1"), ("MBMName", "NAME"), ("ICNo", "IC9"), ("CLMTotalActual", 1500m), ("CLMTotalApproved", 1200.5m));
        var line = ClaimAsciiLayout.ClaimLine(row, rejected: false);
        Assert.EndsWith("\r\r\n", line);
        var body = line[..^3];
        Assert.Equal("A", body[..1]);
        Assert.Equal("B001           ", body[1..16]);
        Assert.Equal("02012024", body[16..24]);
        Assert.Equal("WM", body[24..26]);
        Assert.Equal("WM24000001     ", body[26..41]);
        Assert.Equal("WM24000001-2   ", body[41..56]);
        // Same record without a patient IC is 10 characters longer (legacy quirk, kept for the receiving systems).
        var noIc = ClaimAsciiLayout.ClaimLine(Row(("CasNumber", "WM24000001"), ("ClaimVersion", 2)), rejected: false);
        Assert.Equal(line.Length + 10, noIc.Length);
        Assert.Contains("1,500.00    1,200.50    ", body);
    }

    [Fact]
    public void Rejected_claims_leave_the_bordx_and_amount_fields_blank_and_add_the_repudiation_codes()
    {
        var line = ClaimAsciiLayout.ClaimLine(Row(("PAYCode", "WM"), ("CasNumber", "WM24000009"), ("ICNo", "IC"), ("CASREPCode1", "R01"), ("CASReserveAmount", 800m), ("CLMTotalActual", 99m)), rejected: true);
        Assert.StartsWith("N" + new string(' ', 23) + "WMWM24000009     " + new string(' ', 15), line);
        Assert.DoesNotContain("99.00", line);
        Assert.Contains("R01       " + new string(' ', 20) + "800.00      ", line);
        Assert.Equal(ClaimAsciiLayout.ClaimLine(Row(("ICNo", "IC")), rejected: false).Length, line.Length);
    }

    [Fact]
    public void Case_record_carries_run_date_and_batch_and_defaults_the_amend_status_to_N()
    {
        var line = ClaimAsciiLayout.CaseLine(Row(("PAYCode", "WM"), ("CasNumber", "WM1"), ("ICNo", "IC"), ("CASReserveAmount", 5000.0000m)), new DateTime(2026, 10, 31), "00002");
        Assert.StartsWith("NWMWM1            ", line);
        Assert.Contains("5000    ", line);
        Assert.Contains("3110202600002", line);
        Assert.Equal("WM261031(00002)", ClaimAsciiLayout.FileName("WM", new DateTime(2026, 10, 31), "00002"));
    }
}
