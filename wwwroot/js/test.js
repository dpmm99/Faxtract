/**
 * Performs an end-to-end test by sending two files with different extraContext values
 * to the HomeController.UploadFile endpoint.
 */
function testFileUploadsWithDifferentContexts() {
    console.log("Uploading two files with different extra context...");

    // Some game design stuff Gemini 2.5 Flash wrote.
    const file1 = new File(
        [`### The Unseen Language of Anti-Frustration: Cognitive Load Management via Mechanical Redundancy

We often talk about game design in terms of mechanics, narrative, aesthetics, and player agency. But lurking beneath these grand concepts is a silent, often invisible war waged against player frustration, one that few players ever consciously acknowledge. This battle is particularly interesting when it comes to managing cognitive load, not by simplifying the game, but by subtly introducing mechanical redundancy as a failsafe against accidental failure, thereby preserving the player's perceived agency and immersion.

Consider the classic puzzle game where you need to hit three targets in quick succession with different abilities to open a door. A common source of frustration isn't necessarily the puzzle's complexity, but the "fat finger" moment, the mistimed jump, or the accidental button press that sends you plummeting to your doom, forcing a reset. This isn't a challenge of skill or intellect; it's a momentary lapse, disproportionately punished.

This is where "Cognitive Load Management via Mechanical Redundancy" steps in. It's not about making the game easier in terms of challenge, but *removing unnecessary sources of frustration related to control and minor execution errors*, allowing the player to focus their mental energy on the *intended* challenge.

**How does it manifest?**

1.  **The "Sticky Ledge" or "Magnet Grab":** In many platformers, if you barely miss a ledge, the game "grabs" you onto it for a split second, or if you jump just a hair too early, it extends your jump arc to make the connection. This isn't an accessibility feature in the traditional sense; it's recognizing that the *intended challenge* is the jump's timing or destination, not the microscopic precision of the input. Your brain processes "I jumped, I landed" even if the game subtly corrected a millisecond of imprecision. It reduces the cognitive load of constantly monitoring exact position, freeing up mental bandwidth for strategy or environmental awareness.

2.  **The "Buffered Input Window":** Fighting games, despite their reputation for precision, often employ this. If you input a complex combo slightly before the current animation finishes, the game "buffers" it, executing it at the earliest possible moment. This isn't cheating; it's acknowledging that human reaction time isn't perfectly instantaneous. The *intended challenge* is the knowledge of the combo and its strategic application, not the absolute frame-perfect timing of every single button press. This redundancy of input acceptance across a small time window drastically reduces frustration while preserving the skill ceiling.

3.  **The "Pre-emptive Safe Fall":** Ever notice how in some adventure games, if you walk off a small ledge you *can't* survive, your character often performs a specific animation (like a quick scramble or a gasp) and dies instantly, rather than allowing you to awkwardly flail for a second or two before impact? Or if there's a crucial object you need to grab during a descent, and the game automatically locks your character onto it if you're even vaguely in the vicinity? This isn't about making the game easy, it's about eliminating the pointless, frustrating interlude of a doomed animation sequence, or guaranteeing that an intended interaction isn't missed due to minute positioning. It's about efficiently communicating failure or success, managing the mental resources required to understand the immediate consequence.

4.  **The "Omni-Interact Button":** In some games, instead of separate buttons for "pick up," "open door," "talk to NPC," there's one contextual "interact" button. While seemingly a simplification, it's a form of mechanical redundancy. The *player's intent* is to interact with the object in front of them. Having one button that intelligently handles multiple actions reduces the cognitive load of remembering which button does what in a given context, especially in complex control schemes. The game assumes your most likely intent and filters out other possibilities.

### The Strategic Application of Entropy: Designing for Emergent Imperfection and its Impact on Player Mastery

We often hear about game designers striving for precision, clarity, and tightly controlled experiences. But what if one of the most powerful, yet overlooked, tools in their arsenal is the **deliberate introduction of subtle, controlled imperfection – or entropy – into core game systems?** This isn't about bugs or jank; it's about carefully engineered variations and non-deterministic elements that make the game feel more alive, challenging, and ultimately, more deeply rewarding to master.

Think of it as the opposite side of the coin to "Cognitive Load Management." While the latter removes accidental frustration, the "Strategic Application of Entropy" *adds* a layer of subtle, intended friction and unpredictability, not to frustrate, but to deepen engagement and mastery.

**Why introduce entropy?**

1.  **Enhances Perceived Realism and Organics:** A world where every enemy behaves identically, every jump lands perfectly the same way, and every environmental interaction is clinical and predictable feels sterile. Subtle variations make the world feel lived-in, reactive, and less like a meticulously scripted stage.
2.  **Increases Depth of Mastery:** When a system isn't perfectly predictable, true mastery isn't just about memorizing patterns; it's about adapting, reacting, and developing an intuitive "feel" for the system's inherent variability. It's about learning to exploit or mitigate the *unpredictable* aspects.
3.  **Fosters Emergence:** When individual elements have a slight degree of internal variation, their interactions can lead to genuinely emergent behaviors and unexpected scenarios that even the designers might not have explicitly coded. This leads to unique player stories.
4.  **Prevents Complacency and Boredom:** Absolute predictability leads to rote memorization and eventual disengagement. A touch of entropy keeps the player on their toes, demanding constant attention and adaptation, even within familiar scenarios.
5.  **Manages the "Flow State":** Too much predictability can pull a player *out* of flow by reducing challenge to triviality. A managed level of unpredictability ensures the player remains optimally challenged and engaged.

**Where does this "Strategic Entropy" manifest?**

1.  **AI "Humanization" through Suboptimal Decisions:** Instead of making AI perfectly rational and optimal, designers might intentionally introduce "human-like" imperfections:
    *   **Occasional Hesitation/Over-Aggression:** An enemy might pause for a split second before attacking, or unexpectedly rush in.
    *   **Minor Pathfinding Errors:** Not game-breaking, but just enough to make them feel less like perfectly guided robots.
    *   **Varied Reaction Times:** Sometimes they react instantly, sometimes with a slight delay. This isn't about difficulty settings, but about making them feel less robotic.
    *   **Deliberate Missteps:** A common technique in racing games is for an AI opponent to occasionally "mess up" a corner, creating overtakes or a sense of "realistic" competition without being purely rubber-banded.

2.  **Procedural Generation (Controlled Chaos):** True random generation often feels arbitrary and lifeless. Strategic entropy in procedural generation means defining *constraints* and *rules* for the randomness, ensuring that while the exact layout is unique, it still adheres to a cohesive, believable logic.
    *   **"Fractal Noise" for Terrain:** Not just random height maps, but algorithms that create natural-looking mountains, rivers, and valleys with organic imperfections.
    *   **Layout Variations within Archetypes:** A dungeon might always have a boss room and three sub-areas, but the connections, enemy placements, and trap configurations are slightly different each time. The *feeling* of organic discovery is paramount.
    * 
3.  **Animation & Physics (The "Jitter" and Ragdolls):**
    *   **Subtle Animation Variations:** A character might not perform the exact same "hit reaction" every single time, or their idle animation might have slight, imperceptible shifts.
    *   **Weapon Sway and Recoil:** Not perfectly repeatable. Each shot might have slightly different recoil patterns, making every bullet feel unique and requiring constant micro-adjustments from the player. This is a core component of mastery in many FPS games.
    *   **Ragdoll Physics:** While sometimes comedic, the inherent non-determinism of ragdolls (how a body will crumple after impact) adds a visceral, unpredictable realism that perfectly canned animations can't achieve.

4.  **Environmental Micro-Variations:**
    *   **Weather Systems:** Not just "sunny/rainy" but subtle shifts in intensity, wind direction, light, or particle effects that create unique atmospheric moments.
    *   **Subtle Destructibility:** The way a piece of cover splinters, or a wall crumbles, might vary slightly, making each firefight feel a bit more dynamic.
    *   **NPC Routines:** While an NPC might have a defined patrol, there might be subtle, random variations in their path or timing, making them feel less like automata.

**The "Sweet Spot" and its Elusiveness:**

The true art of Strategic Entropy lies in finding the *sweet spot*.
*   **Too much entropy:** The game feels random, unfair, janky, or broken. Player agency is diminished because success feels like luck.
*   **Too little entropy:** The game feels sterile, predictable, boring, and lacking in emergent depth. Mastery becomes rote memorization.

The balance is paramount. The entropy must be *just enough* to inject dynamism and encourage deeper adaptation, but *not so much* that it undermines the player's ability to learn and feel competent. It’s about "controlled chaos" versus "wild chaos." It's designing for variables that, when combined, create a greater whole than the sum of their predictable parts.

### The Orchestration of Temporal Agency: Designing Turn Order and Initiative Systems as a Core Strategic Pillar

In the realm of turn-based games, the concept of "initiative" or "turn order" is deceptively simple: who gets to act when. On the surface, it's merely a sequencing mechanism. However, a truly profound game design employs this seemingly basic mechanic as a **core strategic pillar**, transforming a utilitarian rule into a dynamic canvas for player agency, anticipation, and emergent tactical depth. This isn't just about "who goes first"; it's about the intricate *choreography* of actions, the psychological weight of waiting, and the deliberate manipulation of temporal flow.

**Beyond Mere Sequencing: What Makes it Strategic?**

The strategic application of turn order moves beyond a simple die roll or static speed comparison. It delves into:

1.  **Predictability vs. Volatility:**
    *   **Highly Predictable (e.g., classic JRPGs with ATB bars filling based on speed, or Chess):** Players can meticulously plan several turns ahead, anticipating enemy actions with high confidence. The challenge lies in optimizing their actions within this fixed sequence. This fosters a sense of intellectual mastery and long-term strategy.
    *   **Highly Volatile (e.g., some tactical RPGs with initiative rolls each round, or games with "interrupt" mechanics):** The turn order can shift dramatically. This introduces an element of risk, demanding adaptability, contingency planning, and immediate tactical responses. The challenge is in reacting to the unknown and exploiting fleeting opportunities.

2.  **Granularity of Action and Interruption:**
    *   Some games divide turns into very granular action points, allowing movement, attacks, and ability uses to be intermingled (e.g., *XCOM*). This offers immense flexibility within a single character's turn.
    *   Others enforce strict "move then act" or "one action per turn" rules.
    *   The most intriguing systems often feature **interrupts, reactions, or readied actions** (e.g., *Divinity: Original Sin 2*, *Pillars of Eternity*). Here, players can deliberately sacrifice initiative or prepare an action to trigger out-of-sequence, injecting a layer of real-time-like reactivity into a turn-based framework. This is a powerful form of "temporal agency," allowing players to bend the rules of time.

3.  **Player-Driven Manipulation and Investment:**
    *   Does a speed stat merely determine turn order, or can players actively *manipulate* it through abilities (e.g., Haste spells, Slow debuffs, delaying a turn to go later)?
    *   Can players "pass" or "overwatch" their turn to react to enemy movements?
    *   Are there abilities that allow a unit to act *again* within a round, or to *steal* an enemy's turn?
    *   These mechanics transform initiative from a passive stat into an active resource and a tactical objective.

**The Deeper Strategic Implications:**

*   **Tempo Control:** The ability to dictate the pace of an engagement. Going first might allow for a critical alpha strike, but going last might allow for a devastating follow-up. Mastering turn order is mastering the *rhythm* of the battle.
*   **Action Economy Management:** Maximizing the number of impactful actions before the enemy can respond. Can you deliver crippling damage before an enemy heals? Can you set up a perfect combo before the enemy breaks your formation?
*   **Target Prioritization:** The sequence of actions is crucial for taking down high-threat targets efficiently. Knowing *when* a powerful enemy will act allows for focused fire or preemptive crowd control.
*   **Buff/Debuff Synchronization:** Ensuring that buffs are applied to your damage dealer *before* their turn, or that debuffs stick to an enemy *before* they act. This requires precise timing within the turn order.
*   **Resource Management (Beyond the Obvious):** Initiative itself can become a resource. Is it worth expending an ability to gain an extra turn, or to deny an enemy their turn?
*   **Psychological Warfare and Anticipation:** The tension of waiting for an enemy's critical turn, or the satisfaction of seeing your perfectly choreographed sequence unfold, is a profound emotional payoff unique to well-designed turn-based systems.
`],
        "test-game-design.txt",
        { type: "text/plain" }
    );
    // Words and phrases I forgot or never knew and had to look up while I was reading Danganronpa Kirigiri volumes 1 through 4 (page 52).
    const file2 = new File(
        [`事件性
ピンからキリ
ノウノウ
付す
一笑
荒唐無稽
往々
道しるべ
あぶりだす
帯同
各界
いたれりつくせり
きな臭い
じれったい
肯く
間怠っこしい
木漏れ日
消化試合
研ぎ澄まし
砥石
占星術
小火
相次ぐ
不審火
奮い立た
受講料
非営利
薬代
伝統掲示板
筆文字
切羽詰まる
密閉
施錠
人里離れた
感傷
しおらしい
安楽椅子
横座り
食べ頃
悄然
あつらえむき
投函
印字
事案
着手金
大仰
仄めかす
書き下ろし
凄惨
待ちわびる
麗しい
目配せ
面食らう
乞う
ひけらかす
オールバック
なよなよ
佇まい
目ぼしい
鬱蒼
キャッチコピー
あたふた
窘める
取り留めのない
痩ける
腕貫
大仰
静謐
矜持
股にかける
古めかしい
気を引き締める
オウム返し
無頓着
昇降口
伝播
一矢を報いる
わらわら
死なばもろとも
害悪
虫唾が走る
まくし立てる
刷り込み
背面
牽制
引火
気化
鑢
講釈
混沌
ボストンバッグ
披露
固唾を飲む
網目
斑点
死斑
猟奇
引っくるめる
壁際
戦慄
細切れ
嗚咽
消去法
すげ替える
奥歯
振り絞る
予見
割り振る
薄ら寒い
儘ならない
愉快犯
我関せず焉
異を唱える
蜃気楼
蘊蓄を傾ける
鏡筒
鬼気
接眼レンズ
どこへともなく
勿体ぶる
暴く
気にかける
役所仕事
差し押さえ
座面
背もたれ
乱歩
増幅
増派
凹面鏡
置き
雪風
暖色
小高い
薄闇
暖をとる
身悶え
竦める
捩り
焦れる
呂律
輩
双子星
一等星
格納
五芒星
風変わり
御仁
ちょっかい
チーク
殺風景
切り揃える
寄せ付ける
あぜ道
解れる
したり顔
ミサ
若気の至り
ヨレヨレ
精悍
短髪
手持ち無沙汰
陣取る
開ける
人家
思わしい

お粧し
埋め尽くす
もみの木
電飾
燭台
冴え冴え
所作
数値化
斯くも
せこい
道楽
晦ます
謳歌
朧気
物狂い
贖罪
門限
平謝り
不覚
ゴツゴツ
鋲
荘厳
誑かす
なす術もない
鷲掴み
しおらしい
組み伏せる
鬼瓦
逡巡
言葉のあや
遵守
はにかむ
立地
拭い去る
中肉中背
封蠟
引ったくる
検める
誘き出す
物色
天板
内壁
シャバい
一事が万事
手並み
鼻白む
気が気でない
華族
大地主
しがない
ドラ息子
真空管
長身痩躯
垣間
現し世
遠巻き
陰惨
杞憂
しな垂れかかる
妖艶
最高潮
ぱっつん
仄めかす
贋作
好々爺
数多
掻い摘む
沈思黙考
大富豪
焼べる
サラ金
手下
はした金
ナップザック
帯封
先立つ
順守
密閉
ほのめかす
生保
退く
隠遁
蔵書
紫南
思わしい
人家
開ける
ろれつが回らない
ちょっかいを出す
チーク
殺風景
切り揃える
あぜ道
解れ
したり顔
ミサ
若気の至り
よれよれ
精かん
短髪
手持ち無沙汰
陣取る
御仁
風変わり
五芒星
格納
一等星
双子星
燕
輩
蜃気楼
雪風
暖色
小高
薄闇
暖をとる
遭難
身悶え
竦める
捩り
焦れる
酌む
置き
埋め込む
凹面鏡
凸面鏡
増派
増幅
乱歩
同形
背もたれ
座面
遇う
差し押さえ
気にかける
暴く
ドスの効いた
勿体ぶる
何処へともかく
めげる
接眼
筒
鬼気
鏡筒
蘊蓄を傾ける
射鏡
異を唱える
我関せず焉
愉快犯
儘ならない
割り振る
予見
下腕
振り替える
振り絞る
奥歯
すげ替える
消去法
細切
嗚咽
戦慄
催促
壁際
引っくるめる
さぞ
猟奇
死斑
斑点
網目
固唾
ボストンバッグ
混沌
講釈
鑢
気化
引火
火傷
牽制
背面
刷り込み
まくし立てる
虫唾が走る
虚しい
害悪
死なばもろとも
わらわら
一矢を報いる
伝播
昇降口
相場が決まっている
顰める
無頓着
オウム返し
気を引き締める
古めかしい
股にかける
矜持
省筆
腕貫
転ける
窘める
キャッチコピー
鬱蒼
指標
目ぼしい
佇まい
抹消
なよなよ
オールバック
ひけらかす
団欒
面食らう
目配せ
麗しい
待ちわびる
凄惨
書き下ろし
取り留めのない
固唾を飲む
冴え冴えした
破格
随天
門扉
ごつごつ
鋲飾り
杉
為す術
鉄柵
誘きだす
しどけない
旧華族
袋状
状
先立
狡猾
率先
お経
漫然
軽んじる
ほっつき歩く
着弾
雷管
弾丸
不謹慎
形振り
がかる
憔悴
恍惚
異存
挙手
細腕
勝算
パイプベッド
頭側
鉄格子
趣向を凝らす
バイスクル
心霊
虫の知らせ
一望
仔細
決る
ずり落ちる
弛緩
扼殺
ピンセット
十八番（おはこ）
柵（しがらみ）
尖らす
不意を突いて
体育座り
血なまぐさい
利便
屈伸
一蹴
白む
爽快
要領
釈然
車座
レトルト
忌み嫌う
通り名
神出鬼没
悪態をつく
従順
懐疑
小突く
二の足を踏む
引率
狼狽える
片栗粉
家鳴り
功を奏する
脇目
事切れる
異音
仮眠
肩車
たくし上げる
啖呵
満を持して
野次
死角
虚空
へろへろ
爆睡
自供
臆する
差し詰め
常套
一丁前
長いものには巻かれる
手玉
積年
直立
天窓
のっぺり
通気口
廃墟然
滞空
平坦
室外機
断崖絶壁
崖っぷち
杭
命綱
臨
難攻不落
理にかなう
皮肉った
白日
撓む
滑車
牽引
静音性
片鱗
頸部
尽くめ
色仕掛け
絆される
与する
算段
すっからかん
日和見
もたつく
睡魔
素通り
撃鉄
瑣末
挙動
弾倉
血痕
詮索
戯ける
どんでん返し
更地
詮索
蠢く
素通り
残像
石畳
かたどる
ご名答
なぞらえる
直向き
土蔵
抜け駆け
並木道
壮麗
迂回
なぞる
空堀
城郭
老若男女
遠巻き
タラップ
不揃い
雑然
スイスイ
尽力
齧る
精神性
厭う
回れ右
凛々しい
持ち場
まやかし
逡巡
凍てつく
確信犯
同田貫
氷塊
凌駕
緩衝材
木箱
非通知
打ち解ける
諜報
ふて腐れる
眉尻
掻い潜る
しどろもどろ
凄味
いとも
手合い
脂汗
回り込み
物色
七分丈
菱形
闘牛
体当たり
咆哮
前のめり
互恵
改ざん
捏造
所以
南京錠
駆り立てる
一揆
小競り合い
曰く
徘徊
生首
象る
廃車
つかぬ事
瓦屋根
呼び鈴
水墨画
板張り
簀の子
リーゼント
チンピラ
お河童
問いただす
鍵穴
籠城
筆入れ
作務衣
甲冑
籠手
手甲
すね当て
草鞋
前立て
面頬
調度品
色紙
黒ずむ
額装
観音開き
真っさら
奥まる
鑑識
厳つい
小宴
拙宅
定石
腕章
見よう見まね
眉根を寄せる
楚々
ふんぞり返る
拱く
予行
幸か不幸か
荒唐無稽
刺々しい
守秘義務
ゴタゴタ
そびれる
挫ける
厳然
これ見よがし
常套
取っ掛かり
粉雪
鬱蒼
奈落
竦む
一溜まりもない
氷柱
袂
茅葺き
石臼
チャンバラ
形振り
笑気
投光器
さらけ出す
擦り付ける
下見
鍔
櫃
宛がう
番える
張り巡らす
落ち窪む
立ちふさがる
初動捜査
消音
つらら
絡繰り
凡百
よじ登る
垂れ込める
だだっ広い
腐乱
離人症
内鍵
筋状
仕打ち
出し抜く
着崩す
常套句
いきさつ
まかり通る
居合い
薙刀
段位
才気煥発
集大成
発足
袂を分かつ
瑣末
後継者
詮索
取りなす
懲り懲り
張り詰める
深入り
割り振る
深追い
踝目深にかぶる
静謐
大広間
縦長
足枷
枷
掻い摘む
南京錠
問い質す
便座
語呂
無頓着
解れる
毛玉
のっぺら坊
一卵性
擦り付ける
無精ひげ
完遂
荒唐無稽
出任せ
躙り寄る
囃し立てる
立ちはだかる
憔悴`],
        "test-japanese-words.txt",
        { type: "text/plain" }
    );

    const formData1 = new FormData();
    formData1.append("files", file1);
    formData1.append("extraContext", "For a game design deck; needs example games.");
    const formData2 = new FormData();
    formData2.append("files", file2);
    formData2.append("extraContext", "In your response, each question should be simply the Japanese word (like just \"Q: 好き\", no 'define' or 'describe'), and the answer should use English to explain its usage, not just the definition.");

    fetch('/Home/UploadFile', {
        method: 'POST',
        body: formData1
    });
    fetch('/Home/UploadFile', {
        method: 'POST',
        body: formData2
    });
}
