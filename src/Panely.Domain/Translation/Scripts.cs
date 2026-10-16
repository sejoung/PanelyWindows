using System.Globalization;
using System.Text;
using Panely.Domain.Model;

namespace Panely.Domain.Translation;

/// <summary>문자 script 분류와 원문 정규화·잡음 제거(06 풍선 그룹과 필터).</summary>
public static class Scripts
{
    public static bool IsKana(char c) =>
        c is >= '぀' and <= 'ゟ'   // Hiragana
        or >= '゠' and <= 'ヿ'     // Katakana (ー 포함)
        or >= 'ㇰ' and <= 'ㇿ'     // Katakana phonetic extensions
        or >= 'ｦ' and <= 'ﾟ';    // Halfwidth katakana

    public static bool IsHan(char c) =>
        c is >= '一' and <= '鿿'
        or >= '㐀' and <= '䶿'
        or >= '豈' and <= '﫿'
        or '々' or '〇';

    public static bool IsHangul(char c) =>
        c is >= '가' and <= '힣'
        or >= 'ᄀ' and <= 'ᇿ'
        or >= '㄰' and <= '㆏';

    public static bool IsAsciiLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    /// <summary>NFKC 후 공백 정리. 줄바꿈은 공백 하나로.</summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var s = text.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(s.Length);
        var lastSpace = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                if (!lastSpace && sb.Length > 0) sb.Append(' ');
                lastSpace = true;
                continue;
            }
            sb.Append(c);
            lastSpace = false;
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// 원문 언어 자체 문자가 있는가. 일본어 kana/han, 중국어 han, 한국어 hangul, 영어 ASCII 알파벳 2개 이상.
    /// 없으면 잡음으로 버린다.
    /// </summary>
    public static bool HasOwnScript(string text, string language)
    {
        switch (language)
        {
            case TranslationLanguages.Japanese:
                foreach (var c in text) if (IsKana(c) || IsHan(c)) return true;
                return false;
            case TranslationLanguages.ChineseSimplified:
            case TranslationLanguages.ChineseTraditional:
                foreach (var c in text) if (IsHan(c)) return true;
                return false;
            case TranslationLanguages.Korean:
                foreach (var c in text) if (IsHangul(c)) return true;
                return false;
            default:
                var n = 0;
                foreach (var c in text) if (IsAsciiLetter(c) && ++n >= 2) return true;
                return false;
        }
    }

    /// <summary>줄 결합: 한글/영어는 공백, 일본어/중국어는 빈 문자열.</summary>
    public static string LineJoiner(string language) =>
        language is TranslationLanguages.Korean or TranslationLanguages.English ? " " : string.Empty;

    /// <summary>일본어/중국어 OCR이 글자 사이에 넣은 공백 제거.</summary>
    public static string RemoveCjkSpaces(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == ' ' && i > 0 && i + 1 < text.Length && !IsAsciiLetter(text[i - 1]) && !IsAsciiLetter(text[i + 1])) continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    public static UnicodeCategory Category(char c) => CharUnicodeInfo.GetUnicodeCategory(c);

    public static bool IsPunctuationOrSymbol(char c)
    {
        var cat = Category(c);
        return cat is UnicodeCategory.OtherPunctuation or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation
            or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation
            or UnicodeCategory.ConnectorPunctuation or UnicodeCategory.MathSymbol or UnicodeCategory.OtherSymbol
            or UnicodeCategory.ModifierSymbol or UnicodeCategory.CurrencySymbol;
    }
}

/// <summary>script 기반 원문 언어 감지(NaturalLanguage 대체).</summary>
public static class LanguageDetector
{
    // 간체 전용으로 흔한 글자(일본어 신자체와 겹치는 글자는 넣지 않는다)
    private const string SimplifiedOnly =
        "这们说么来时会对吗还没过样为后发见现问题话点应开关头实经让认识谁爱东车门马鸟鱼书买卖长张习亲边远进运气电个万与专业两严乐义乡乱争云亚产从价众优传伤体儿党兰兴养军农决净减几凤击划刘则刚创别剧劝办务动励劳势区医华协单卫厂厅历压县参双变叶号叹听启员响团园围图圆圣场坏块坚坛墙壮声壳处备复够夸夹夺奋奖妇妈婴孙宁宝宠审宪宫宽宾寻导寿将尔尘尝尽层属岁岛岭币师帐带帮庄庆库庙废异弃弹强归当录彻忆忧怀态恋恶恼悦悬惊惧惨惯愿懒戏战户扑执扩扫扬扰抛抢护报担拟拥拦拨择挂挡挤挥损捡换据摆摇摊撑敌数斋斗斩断无旧显晒晓晕暂术机杀杂权条杨杰极构枪标栈树桥梦检楼欢欧残毁毕汇汉汤沟泪泼泽洁洒浅浆浊测济浑浓涛涨温湾湿满滤滥滩潜澜灭灯灵灾灿炉炼烂烟烦烧热焕爷牵牺状独狱猪猫献环画畅疗疯痒皱盐监盖盗盘睁瞒矿码砖确碍礼祸禅离种积称稳穷窃窍窝竖竞笔筑筛筹签简篮类粮紧红约级纪纯纱纲纳纵纸纹纺线练组细织终绍经结绕绘给络绝统绢绣继绩绪续维绵绿编缘缝缩缴网罗罚罢联职聪肃肠肤肿胀胆胜胶脉脏脑脚脱脸舰艰艳艺节苏苹茎荐药莲获营萧萨葱蓝虫虽虾蚂蚕蛮蜡蝇蝉补衬袜袭装裤观规觉览触誉计订讨训议讯记讲许论设访证评识诈诉诊词译试诗诚详该语误诱请诸诺读课调谈谋谎谢谣谦谨谱贝负贡财责贤败账货质贩贫购贮贯贴贵贷贸费贺贼贾资赋赌赏赔赖赚赛赞赠赢赵赶趋跃践踪躯辆输轻载较辅辈辉轮软转辞辩辽达迁迈违连迟迹适选逊递逻遗遥邓邮邻郑酿释鉴针钉钓钟钢钥铃铅铁铜铝银铸链销锁锅锈锋错锚键锯镜闪闭闯闲间闷闹闻阅阔队阳阴阵阶际陆陈险随隐难雾静韦韩页顶项顺须顽顾顿颁预领颇颈频颜额颠飘飞饥饭饮饰饱饲饼饿馆馒驾驶驱骑骗验鸡鸣鸭鹅鹰齐齿龄龙龟";

    private const string TraditionalOnly =
        "這們說麼來時國會對嗎還沒過樣為後發見現問題話點應開關頭實經讓認識誰愛東車門馬鳥魚書買賣長張學習親邊遠進運氣電個萬與專業兩嚴樂義鄉亂爭雲亞產從價眾優傳傷體兒黨蘭興養內寫軍農決淨減幾鳳擊劃劉則剛創別劇勸辦務動勵勞勢區醫華協單衛廠廳歷壓縣參雙變葉號嘆聽啟員響團園圍圖圓聖場壞塊堅壇牆壯聲殼處備復夠誇夾奪奮獎婦媽嬰孫寧寶寵審憲宮寬賓尋導壽將爾塵嘗盡層屬歲島嶺幣師帳帶幫莊慶庫廟廢異棄彈強歸當錄徹憶憂懷態戀惡惱悅懸驚懼慘慣願懶戲戰戶撲執擴掃揚擾拋搶護報擔擬擁攔撥擇掛擋擠揮損撿換據擺搖攤撐敵數齋鬥斬斷無舊顯曬曉暈暫術機殺雜權條楊傑極構槍標棧樹橋夢檢樓歡歐殘毀畢匯漢湯溝淚潑澤潔灑淺漿濁測濟渾濃濤漲溫灣濕滿濾濫灘潛瀾滅燈靈災燦爐煉爛煙煩燒熱煥爺牽犧狀獨獄豬貓獻環畫暢療瘋癢皺鹽監蓋盜盤睜瞞礦碼磚確礙禮禍禪離種積稱穩窮竊竅窩豎競筆築篩籌簽簡籃類糧緊紅約級紀純紗綱納縱紙紋紡線練組細織終紹經結繞繪給絡絕統絹繡繼績緒續維綿綠編緣縫縮繳網羅罰罷聯職聰肅腸膚腫脹膽勝膠脈臟腦腳脫臉艦艱豔藝節蘇蘋莖薦藥蓮獲營蕭薩蔥藍蟲雖蝦螞蠶蠻蠟蠅蟬補襯襪襲裝褲觀規覺覽觸譽計訂討訓議訊記講許論設訪證評詐訴診詞譯試詩誠詳該語誤誘請諸諾讀課調談謀謊謝謠謙謹譜貝負貢財責賢敗賬貨質販貧購貯貫貼貴貸貿費賀賊賈資賦賭賞賠賴賺賽讚贈贏趙趕趨躍踐蹤軀輛輸輕載較輔輩輝輪軟轉辭辯遼達遷邁違連遲跡適選遜遞邏遺遙鄧郵鄰鄭釀釋鑑針釘釣鐘鋼鑰鈴鉛鐵銅鋁銀鑄鏈銷鎖鍋鏽鋒錯錨鍵鋸鏡閃閉闖閒間悶鬧聞閱闊隊陽陰陣階際陸陳險隨隱難霧靜韋韓頁頂項順須頑顧頓頒預領頗頸頻顏額顛飄飛飢飯飲飾飽飼餅餓館饅駕駛驅騎騙驗雞鳴鴨鵝鷹齊齒齡龍龜";

    private static readonly HashSet<char> SimplifiedSet = [.. SimplifiedOnly];
    private static readonly HashSet<char> TraditionalSet = [.. TraditionalOnly];

    /// <summary>
    /// 여러 원문 조각에서 언어를 추정한다. kana가 있으면 일본어, hangul 우세면 한국어,
    /// han만 있으면 간체/번체 전용 글자 수로 중국어 변형, 라틴 알파벳만 있으면 영어. 근거가 부족하면 null.
    /// </summary>
    public static string? Detect(IEnumerable<string> texts)
    {
        int kana = 0, han = 0, hangul = 0, latin = 0, simp = 0, trad = 0;
        foreach (var t in texts)
        {
            foreach (var c in t)
            {
                if (Scripts.IsKana(c) && c != 'ー') kana++;
                else if (Scripts.IsHan(c))
                {
                    han++;
                    if (SimplifiedSet.Contains(c)) simp++;
                    if (TraditionalSet.Contains(c)) trad++;
                }
                else if (Scripts.IsHangul(c)) hangul++;
                else if (Scripts.IsAsciiLetter(c)) latin++;
            }
        }
        var cjk = kana + han + hangul;
        if (kana >= 2 && kana >= hangul) return TranslationLanguages.Japanese;
        if (hangul >= 2 && hangul >= han) return TranslationLanguages.Korean;
        if (han >= 2) return trad > simp ? TranslationLanguages.ChineseTraditional : TranslationLanguages.ChineseSimplified;
        if (cjk == 0 && latin >= 4) return TranslationLanguages.English;
        if (hangul > 0) return TranslationLanguages.Korean;
        if (kana > 0) return TranslationLanguages.Japanese;
        return null;
    }

    /// <summary>번역 결과가 대상 언어로 보이는가(다른 언어 결과를 성공으로 캐시하지 않기 위한 보수적 검사).</summary>
    public static bool LooksLikeLanguage(string text, string language)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        int kana = 0, han = 0, hangul = 0, latin = 0;
        foreach (var c in text)
        {
            if (Scripts.IsKana(c)) kana++;
            else if (Scripts.IsHan(c)) han++;
            else if (Scripts.IsHangul(c)) hangul++;
            else if (Scripts.IsAsciiLetter(c)) latin++;
        }
        var total = kana + han + hangul + latin;
        if (total == 0) return true; // 숫자·기호만: 그대로 허용
        return language switch
        {
            TranslationLanguages.Korean => hangul > 0 || (latin > 0 && kana + han == 0),
            TranslationLanguages.Japanese => kana + han > 0 || (latin > 0 && hangul == 0),
            TranslationLanguages.English => latin > 0 && hangul + kana == 0,
            _ => han > 0 || (latin > 0 && hangul + kana == 0),
        };
    }
}
