using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace WarpTalk.AuthService.Application.Helpers;

/// <summary>
/// WT-888 — random read-aloud phrases for the live voice-profile recording.
///
/// A phrase is a handful of everyday words drawn without repetition from a per-language list
/// with a cryptographic RNG: easy to read aloud, impossible to have been said in a recording made
/// before the phrase existed. 8 words from 100 is ~10^15 phrases.
///
/// The words are concrete nouns a speech-to-text model spells one way. Japanese uses katakana
/// loanwords for that reason — a kanji word can come back in kana, or the other way round, and
/// the matcher compares characters. Vietnamese words are common two-syllable nouns; the matcher
/// drops tone marks, so a misheard tone does not fail an honest reading.
///
/// Languages without a list (none today: profiles are vi, en and ja) fall back to English.
/// </summary>
public static class VoiceChallengePhrases
{
    public const string FallbackLanguage = "en";

    private sealed record Bank(int WordCount, string Separator, IReadOnlyList<string> Words);

    private static readonly IReadOnlyDictionary<string, Bank> Banks = new Dictionary<string, Bank>
    {
        ["en"] = new(8, " ", new[]
        {
            "apple", "river", "window", "garden", "yellow", "orange", "mountain", "pencil",
            "rabbit", "guitar", "castle", "candle", "silver", "purple", "forest", "island",
            "butter", "cookie", "jacket", "ladder", "marble", "needle", "ocean", "pepper",
            "pillow", "planet", "rocket", "saddle", "tiger", "tomato", "tunnel", "velvet",
            "wagon", "whistle", "winter", "zebra", "anchor", "basket", "blanket", "bridge",
            "button", "camera", "carpet", "cherry", "circle", "copper", "cotton", "dragon",
            "engine", "falcon", "feather", "finger", "flower", "hammer", "harbor", "helmet",
            "honey", "kettle", "kitten", "lemon", "lizard", "magnet", "meadow", "mirror",
            "monkey", "muffin", "napkin", "noodle", "orchard", "paddle", "parrot", "peanut",
            "pebble", "pocket", "puzzle", "ribbon", "saucer", "shadow", "spider", "summer",
            "sunset", "thunder", "ticket", "timber", "turtle", "umbrella", "violin", "walnut",
            "wizard", "yogurt", "balloon", "bicycle", "chimney", "dolphin", "elephant", "lantern",
            "penguin", "pumpkin", "sandwich", "tractor",
        }),
        ["vi"] = new(7, " ", new[]
        {
            "quả cam", "cái bàn", "con mèo", "dòng sông", "ngọn núi", "bầu trời", "cơn mưa",
            "mặt trời", "cánh đồng", "con thuyền", "ngôi nhà", "cây cầu", "bông hoa",
            "quyển sách", "cây bút", "đôi giày", "chiếc áo", "con đường", "lá cây", "hạt gạo",
            "bát cơm", "ly nước", "tách trà", "cà phê", "bánh mì", "quả chuối", "quả táo",
            "con cá", "con chim", "con gà", "con chó", "đồng hồ", "điện thoại", "máy tính",
            "bàn phím", "cửa sổ", "ngọn đèn", "bức tranh", "chiếc ô", "cái ghế", "tủ lạnh",
            "bếp lửa", "nồi canh", "mùa xuân", "mùa hè", "mùa thu", "mùa đông", "buổi sáng",
            "buổi tối", "sân trường", "công viên", "thư viện", "bệnh viện", "phố cổ", "hồ nước",
            "con voi", "con hổ", "con thỏ", "con rùa", "con ngựa", "cái kéo", "cái thang",
            "cái nón", "cái gối", "tấm thảm", "chiếc xe", "xe đạp", "máy bay", "tàu hỏa",
            "bãi biển", "hòn đảo", "khu rừng", "thác nước", "cầu vồng", "ngôi sao", "mặt trăng",
            "đám mây", "hạt mưa", "bông tuyết", "cây đàn", "quả bóng", "con diều", "cái trống",
        }),
        ["ja"] = new(6, "、", new[]
        {
            "テレビ", "カメラ", "ピアノ", "バナナ", "トマト", "コーヒー", "ノート", "ギター",
            "ホテル", "タクシー", "ケーキ", "ボール", "ドア", "ペン", "メロン", "レモン",
            "チーズ", "バス", "ラジオ", "ミルク", "ジュース", "スープ", "サラダ", "テニス",
            "ゴルフ", "スキー", "ダンス", "ゲーム", "ニュース", "メール", "ベッド", "シャツ",
            "コート", "ネクタイ", "バッグ", "カップ", "フォーク", "ナイフ", "スプーン", "ボタン",
            "ロボット", "ロケット", "エンジン", "タオル", "ソファ", "カーテン", "ランプ", "ポスト",
            "エレベーター", "パイナップル", "オレンジ", "キャベツ", "ピザ", "ハンバーガー",
            "サンドイッチ", "チョコレート", "アイスクリーム", "ヨーグルト", "パソコン", "マウス",
        }),
    };

    /// <summary>The bare language a phrase is written in for a profile language tag ("vi-VN" → "vi").</summary>
    public static string PhraseLanguageFor(string? language)
    {
        var bare = (language ?? string.Empty).Trim().Split('-', '_')[0].ToLowerInvariant();
        return Banks.ContainsKey(bare) ? bare : FallbackLanguage;
    }

    public static string Generate(string phraseLanguage)
    {
        var bank = Banks.TryGetValue(phraseLanguage, out var found) ? found : Banks[FallbackLanguage];
        var pool = bank.Words.ToList();
        var picked = new List<string>(bank.WordCount);
        for (var i = 0; i < bank.WordCount && pool.Count > 0; i++)
        {
            var index = RandomNumberGenerator.GetInt32(pool.Count);
            picked.Add(pool[index]);
            pool.RemoveAt(index);
        }
        return string.Join(bank.Separator, picked);
    }
}
