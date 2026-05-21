using System.Runtime.CompilerServices;
using System.Text.Json;
using FraudScoreApi.Storage;

namespace FraudScoreApi.FraudScore;

/// <summary>
/// Parses a `POST /fraud-score` body straight from UTF-8 bytes into the 14-dim vector,
/// skipping the DTO allocation path used by <see cref="System.Text.Json.JsonSerializer"/>
/// source-gen. Saves ~250 B of heap allocations per request and avoids
/// <see cref="DateTimeOffset.Parse"/> on the hot path (uses fixed-position ISO 8601 parse).
/// </summary>
internal static class FraudScoreParser
{
    private const double MaxAmount = 10000.0;
    private const double MaxInstallments = 12.0;
    private const double AmountVsAvgRatio = 10.0;
    private const double MaxMinutes = 1440.0;
    private const double MaxKm = 1000.0;
    private const double MaxTxCount24h = 20.0;
    private const double MaxMerchantAvgAmount = 10000.0;

    private const int MaxKnownMerchants = 32;

    /// <summary>Returns false on malformed JSON or missing required fields.</summary>
    public static bool TryParseAndVectorize(ReadOnlySpan<byte> json, Span<float> vector)
    {
        // ---- Locals: zero-alloc state ----
        double txAmount = 0, custAvg = 0, merchAvg = 0, kmFromHome = 0, lastKm = 0;
        int txInstallments = 0, custTxCount = 0;
        int txY = 0, txMo = 0, txD = 0, txH = 0, txMi = 0, txS = 0;
        int lastY = 0, lastMo = 0, lastD = 0, lastH = 0, lastMi = 0, lastS = 0;
        bool isOnline = false, cardPresent = false;
        bool hasTx = false, hasCust = false, hasMerch = false, hasTerm = false;
        bool hasTxA = false, hasTxI = false, hasTxR = false;
        bool hasCustA = false, hasCustC = false;
        bool hasMerchId = false, hasMerchMcc = false, hasMerchA = false;
        bool hasOnline = false, hasCard = false, hasKm = false;
        bool hasLast = false, hasLastTs = false, hasLastKm = false;

        Span<int> kmStarts = stackalloc int[MaxKnownMerchants];
        Span<int> kmLens = stackalloc int[MaxKnownMerchants];
        int kmCount = 0;

        int merchIdStart = -1, merchIdLen = 0;
        int merchMccStart = -1, merchMccLen = 0;

        // scope: 0=root, 1=transaction, 2=customer, 3=merchant, 4=terminal, 5=last_transaction
        int scope = 0;

        var reader = new Utf8JsonReader(json, isFinalBlock: true, state: default);

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (reader.CurrentDepth == 0) break;
                scope = 0;
                continue;
            }
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            var name = reader.ValueSpan;

            switch (scope)
            {
                case 0:
                    if (name.SequenceEqual("transaction"u8))
                    {
                        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
                        hasTx = true; scope = 1;
                    }
                    else if (name.SequenceEqual("customer"u8))
                    {
                        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
                        hasCust = true; scope = 2;
                    }
                    else if (name.SequenceEqual("merchant"u8))
                    {
                        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
                        hasMerch = true; scope = 3;
                    }
                    else if (name.SequenceEqual("terminal"u8))
                    {
                        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
                        hasTerm = true; scope = 4;
                    }
                    else if (name.SequenceEqual("last_transaction"u8))
                    {
                        if (!reader.Read()) return false;
                        if (reader.TokenType == JsonTokenType.Null) { hasLast = false; }
                        else if (reader.TokenType == JsonTokenType.StartObject) { hasLast = true; scope = 5; }
                        else return false;
                    }
                    else
                    {
                        // Unknown root field (e.g. "id") — skip
                        if (!reader.Read()) return false;
                        reader.Skip();
                    }
                    break;

                case 1: // transaction
                    if (name.SequenceEqual("amount"u8))
                    {
                        if (!reader.Read() || reader.TokenType != JsonTokenType.Number) return false;
                        txAmount = reader.GetDouble(); hasTxA = true;
                    }
                    else if (name.SequenceEqual("installments"u8))
                    {
                        if (!reader.Read() || reader.TokenType != JsonTokenType.Number) return false;
                        txInstallments = reader.GetInt32(); hasTxI = true;
                    }
                    else if (name.SequenceEqual("requested_at"u8))
                    {
                        if (!reader.Read() || reader.TokenType != JsonTokenType.String) return false;
                        if (!TryParseIso8601(reader.ValueSpan, out txY, out txMo, out txD, out txH, out txMi, out txS))
                            return false;
                        hasTxR = true;
                    }
                    else { if (!reader.Read()) return false; reader.Skip(); }
                    break;

                case 2: // customer
                    if (name.SequenceEqual("avg_amount"u8))
                    {
                        if (!reader.Read() || reader.TokenType != JsonTokenType.Number) return false;
                        custAvg = reader.GetDouble(); hasCustA = true;
                    }
                    else if (name.SequenceEqual("tx_count_24h"u8))
                    {
                        if (!reader.Read() || reader.TokenType != JsonTokenType.Number) return false;
                        custTxCount = reader.GetInt32(); hasCustC = true;
                    }
                    else if (name.SequenceEqual("known_merchants"u8))
                    {
                        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return false;
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                        {
                            if (reader.TokenType == JsonTokenType.String && kmCount < MaxKnownMerchants)
                            {
                                kmStarts[kmCount] = (int)reader.TokenStartIndex + 1;
                                kmLens[kmCount] = reader.ValueSpan.Length;
                                kmCount++;
                            }
                        }
                    }
                    else { if (!reader.Read()) return false; reader.Skip(); }
                    break;

                case 3: // merchant
                    if (name.SequenceEqual("id"u8))
                    {
                        if (!reader.Read() || reader.TokenType != JsonTokenType.String) return false;
                        merchIdStart = (int)reader.TokenStartIndex + 1;
                        merchIdLen = reader.ValueSpan.Length;
                        hasMerchId = true;
                    }
                    else if (name.SequenceEqual("mcc"u8))
                    {
                        if (!reader.Read() || reader.TokenType != JsonTokenType.String) return false;
                        merchMccStart = (int)reader.TokenStartIndex + 1;
                        merchMccLen = reader.ValueSpan.Length;
                        hasMerchMcc = true;
                    }
                    else if (name.SequenceEqual("avg_amount"u8))
                    {
                        if (!reader.Read() || reader.TokenType != JsonTokenType.Number) return false;
                        merchAvg = reader.GetDouble(); hasMerchA = true;
                    }
                    else { if (!reader.Read()) return false; reader.Skip(); }
                    break;

                case 4: // terminal
                    if (name.SequenceEqual("is_online"u8))
                    {
                        if (!reader.Read()) return false;
                        if (reader.TokenType == JsonTokenType.True) isOnline = true;
                        else if (reader.TokenType == JsonTokenType.False) isOnline = false;
                        else return false;
                        hasOnline = true;
                    }
                    else if (name.SequenceEqual("card_present"u8))
                    {
                        if (!reader.Read()) return false;
                        if (reader.TokenType == JsonTokenType.True) cardPresent = true;
                        else if (reader.TokenType == JsonTokenType.False) cardPresent = false;
                        else return false;
                        hasCard = true;
                    }
                    else if (name.SequenceEqual("km_from_home"u8))
                    {
                        if (!reader.Read() || reader.TokenType != JsonTokenType.Number) return false;
                        kmFromHome = reader.GetDouble(); hasKm = true;
                    }
                    else { if (!reader.Read()) return false; reader.Skip(); }
                    break;

                case 5: // last_transaction
                    if (name.SequenceEqual("timestamp"u8))
                    {
                        if (!reader.Read() || reader.TokenType != JsonTokenType.String) return false;
                        if (!TryParseIso8601(reader.ValueSpan, out lastY, out lastMo, out lastD, out lastH, out lastMi, out lastS))
                            return false;
                        hasLastTs = true;
                    }
                    else if (name.SequenceEqual("km_from_current"u8))
                    {
                        if (!reader.Read() || reader.TokenType != JsonTokenType.Number) return false;
                        lastKm = reader.GetDouble(); hasLastKm = true;
                    }
                    else { if (!reader.Read()) return false; reader.Skip(); }
                    break;
            }
        }

        // ---- Validate required ----
        if (!hasTx || !hasCust || !hasMerch || !hasTerm) return false;
        if (!hasTxA || !hasTxI || !hasTxR) return false;
        if (!hasCustA || !hasCustC) return false;
        if (!hasMerchId || !hasMerchMcc || !hasMerchA) return false;
        if (!hasOnline || !hasCard || !hasKm) return false;
        if (hasLast && (!hasLastTs || !hasLastKm)) return false;

        // ---- Compute the 14-dim vector ----
        vector[0] = ClampF(txAmount / MaxAmount);
        vector[1] = ClampF(txInstallments / MaxInstallments);
        vector[2] = (float)(custAvg > 0
            ? Clamp01(txAmount / custAvg / AmountVsAvgRatio)
            : 1.0);
        vector[3] = (float)(txH / 23.0);
        vector[4] = (float)(DayOfWeekMonStart(txY, txMo, txD) / 6.0);

        if (hasLast)
        {
            var reqTicks = new DateTime(txY, txMo, txD, txH, txMi, txS, DateTimeKind.Utc).Ticks;
            var lastTicks = new DateTime(lastY, lastMo, lastD, lastH, lastMi, lastS, DateTimeKind.Utc).Ticks;
            double minutes = (reqTicks - lastTicks) / (double)TimeSpan.TicksPerMinute;
            vector[5] = ClampF(minutes / MaxMinutes);
            vector[6] = ClampF(lastKm / MaxKm);
        }
        else
        {
            vector[5] = -1f;
            vector[6] = -1f;
        }

        vector[7] = ClampF(kmFromHome / MaxKm);
        vector[8] = ClampF(custTxCount / MaxTxCount24h);
        vector[9] = isOnline ? 1f : 0f;
        vector[10] = cardPresent ? 1f : 0f;

        // unknown_merchant: 1 if merchant.id NOT in known_merchants, else 0
        var merchId = json.Slice(merchIdStart, merchIdLen);
        bool isKnown = false;
        for (int i = 0; i < kmCount; i++)
        {
            if (json.Slice(kmStarts[i], kmLens[i]).SequenceEqual(merchId)) { isKnown = true; break; }
        }
        vector[11] = isKnown ? 0f : 1f;

        vector[12] = (float)GetMccRisk(json.Slice(merchMccStart, merchMccLen));
        vector[13] = ClampF(merchAvg / MaxMerchantAvgAmount);

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ClampF(double x) => (float)(x < 0.0 ? 0.0 : x > 1.0 ? 1.0 : x);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Clamp01(double x) => x < 0.0 ? 0.0 : x > 1.0 ? 1.0 : x;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsDigit(byte b) => b >= (byte)'0' && b <= (byte)'9';

    /// <summary>Expects exactly "YYYY-MM-DDTHH:MM:SSZ" (length ≥ 19, byte-positional).</summary>
    private static bool TryParseIso8601(
        ReadOnlySpan<byte> s,
        out int y, out int m, out int d,
        out int h, out int min, out int sec)
    {
        y = m = d = h = min = sec = 0;
        if (s.Length < 19) return false;
        if (!IsDigit(s[0]) || !IsDigit(s[1]) || !IsDigit(s[2]) || !IsDigit(s[3])) return false;
        if (s[4] != (byte)'-') return false;
        if (!IsDigit(s[5]) || !IsDigit(s[6])) return false;
        if (s[7] != (byte)'-') return false;
        if (!IsDigit(s[8]) || !IsDigit(s[9])) return false;
        if (s[10] != (byte)'T') return false;
        if (!IsDigit(s[11]) || !IsDigit(s[12])) return false;
        if (s[13] != (byte)':') return false;
        if (!IsDigit(s[14]) || !IsDigit(s[15])) return false;
        if (s[16] != (byte)':') return false;
        if (!IsDigit(s[17]) || !IsDigit(s[18])) return false;

        y = (s[0] - '0') * 1000 + (s[1] - '0') * 100 + (s[2] - '0') * 10 + (s[3] - '0');
        m = (s[5] - '0') * 10 + (s[6] - '0');
        d = (s[8] - '0') * 10 + (s[9] - '0');
        h = (s[11] - '0') * 10 + (s[12] - '0');
        min = (s[14] - '0') * 10 + (s[15] - '0');
        sec = (s[17] - '0') * 10 + (s[18] - '0');
        return true;
    }

    /// <summary>
    /// Sakamoto's algorithm, returns Mon=0..Sun=6 (matches REGRAS_DE_DETECCAO.md).
    /// </summary>
    private static int DayOfWeekMonStart(int y, int m, int d)
    {
        ReadOnlySpan<int> t = [0, 3, 2, 5, 0, 3, 5, 1, 4, 6, 2, 4];
        if (m < 3) y -= 1;
        int dow = (y + y / 4 - y / 100 + y / 400 + t[m - 1] + d) % 7; // 0=Sun..6=Sat
        return (dow + 6) % 7; // shift to Mon=0..Sun=6
    }

    /// <summary>MCC → risk, byte-level (no string allocations).</summary>
    private static double GetMccRisk(ReadOnlySpan<byte> mcc)
    {
        // Length-prefix to make the per-MCC compare cheap (avoids 10 sequence-equals in worst case).
        if (mcc.Length != 4) return 0.50;
        if (mcc.SequenceEqual("5411"u8)) return 0.15;
        if (mcc.SequenceEqual("5812"u8)) return 0.30;
        if (mcc.SequenceEqual("5912"u8)) return 0.20;
        if (mcc.SequenceEqual("5944"u8)) return 0.45;
        if (mcc.SequenceEqual("7801"u8)) return 0.80;
        if (mcc.SequenceEqual("7802"u8)) return 0.75;
        if (mcc.SequenceEqual("7995"u8)) return 0.85;
        if (mcc.SequenceEqual("4511"u8)) return 0.35;
        if (mcc.SequenceEqual("5311"u8)) return 0.25;
        if (mcc.SequenceEqual("5999"u8)) return 0.50;
        return 0.50;
    }
}
