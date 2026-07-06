namespace Re0Agent.Core.Database;

public sealed record SqlSheetDefinition(
    string Uid,
    string Name,
    string Note,
    string InitNode,
    string DeleteNode,
    string UpdateNode,
    string InsertNode,
    string Ddl,
    IReadOnlyList<IReadOnlyList<string>> Content);

/// <summary>
/// 内置骰子表格 SQL 说明书。运行时不再依赖外部 SQL 表格 JSON。
/// </summary>
public static class SqlSheetCatalog
{
    public static IReadOnlyList<SqlSheetDefinition> Sheets { get; } =
    [
        new SqlSheetDefinition(
            Uid: "sheet_global_data",
            Name: "全局数据表",
            Note: """"
            记录当前世界的核心状态。全表只有一行，不增不删。
            
            【列定义】
            列1=row_id（仅允许为 1）
            列2=当前详细地点 current_location
            列3=当前次要地区 current_minor_region
            列4=当前主要地区 current_major_region
            列5=上轮场景时间 prev_scene_time（可 NULL）
            列6=经过的时间 elapsed_time
            列7=当前时间 cur_time
            列8=是否色色is_lewd
            
            【地点层级】
            从小到大：详细地点 < 次要地区 < 主要地区
            示例：御苑（详细）< 新宿区（次要）< 东京都（主要）
            
            【地点填写规则】
            每个字段只写该层级名称，不带前缀。
            对：当前详细地点填 "御苑"
            错：当前详细地点填 "东京-新宿区-御苑"
            
            【时间格式】
            当前时间、上轮场景时间："<月>-<日>-<时>:<分>"，例如 "塔姆兹月-14日-??:??"。
            未知的时或分用 "??" 占位；禁止写 "上午"、"下午"、"早晨" 等自然时段词。
            经过的时间："{数值}{单位}"，多单位直接连写，不加空格。
            单位示例：[年,月,周,天,小时,分]
            示例："3小时20分" | "2天" | "3年6月"
            公式：当前时间 = 上轮场景时间 + 经过的时间
            
            【位置同步提示】
            - 全局数据表中的各级地点需要与世界地图点表、地图元素表、主角表、重要角色表中位置相关信息保持一致
            
            【是否色色判断】
            根据当前情景判断，只能填是或否。
            """",
            InitNode: """
            插入唯一的一行，填入初始时间和位置。
            SQL示例: INSERT INTO global_state (row_id, current_location, current_minor_region, current_major_region, prev_scene_time, elapsed_time, cur_time, is_lewd) VALUES (1, '御苑', '新宿区', '东京都', NULL, '0分', '塔姆兹月-14日-??:??','否');
            """,
            DeleteNode: """
            禁止。
            """,
            UpdateNode: """
            每轮都要更新：上轮场景时间、经过的时间、当前时间。位置有变就更新对应地点字段。current_location、current_minor_region、current_major_region、elapsed_time、cur_time、is_lewd均 NOT NULL，不能写成 NULL 或空串。cur_time / prev_scene_time 必须严格是 '<月>-<日>-<时>:<分>' 格式；未知时分写 '??:??'，禁止写自然时段词。
            SQL示例(纯时间推进): UPDATE global_state SET prev_scene_time = '塔姆兹月-14日-??:??', elapsed_time = '3小时20分', cur_time = '塔姆兹月-14日-??:??', is_lewd = '否' WHERE row_id = 1;
            SQL示例(含位置变动): UPDATE global_state SET current_location = '御苑', current_minor_region = '新宿区', current_major_region = '东京都', prev_scene_time = '塔姆兹月-14日-??:??', elapsed_time = '1小时', cur_time = '塔姆兹月-14日-??:??', is_lewd = '否' WHERE row_id = 1;
            """,
            InsertNode: """
            禁止。
            """,
            Ddl: """
            CREATE TABLE global_state ( -- 全局数据表
              row_id INTEGER PRIMARY KEY CHECK(row_id = 1), -- 行号，仅允许为 1
              current_location TEXT NOT NULL, -- 当前详细地点
              current_minor_region TEXT NOT NULL, -- 当前次要地区
              current_major_region TEXT NOT NULL, -- 当前主要地区
              prev_scene_time TEXT CHECK(prev_scene_time IS NULL OR (prev_scene_time GLOB '*月-*日-*:*' AND instr(prev_scene_time, '上午') = 0 AND instr(prev_scene_time, '下午') = 0 AND instr(prev_scene_time, '早晨') = 0)), -- 上轮场景时间
              elapsed_time TEXT NOT NULL, -- 经过的时间
              cur_time TEXT NOT NULL CHECK(cur_time GLOB '*月-*日-*:*' AND instr(cur_time, '上午') = 0 AND instr(cur_time, '下午') = 0 AND instr(cur_time, '早晨') = 0), -- 当前时间
              is_lewd TEXT NOT NULL DEFAULT '否' CHECK(is_lewd IN ('是', '否')) -- 是否色色
            );
            """,
            Content: [["row_id", "当前详细地点", "当前次要地区", "当前主要地区", "上轮场景时间", "经过的时间", "当前时间", "是否色色"]]),
        new SqlSheetDefinition(
            Uid: "sheet_world_map",
            Name: "世界地图点",
            Note: """"
            记录当前主要地区内的所有次要地区和详细地点。列顺序从小到大：详细地点 → 次要地区 → 主要地区。
            
            【列定义】
            列1=row_id
            列2=详细地点 location_name（全表唯一，作为其他表引用地点的基准）
            列3=次要地区 minor_region
            列4=主要地区 major_region
            列5=地点类型 location_type
            列6=环境描述 environment_desc（≤60 字）
            列7=重要度 importance
            列8=探索状态 exploration_status
            
            【地点填写规则】
            每个字段只写该层级名称，不带前缀。
            对：详细地点填 "御苑"，次要地区填 "新宿区"，主要地区填 "东京都"
            错：详细地点填 "东京-新宿区-御苑" 或 "新宿区-御苑"
            
            【主键判定（判断 insert 还是 update）】
            收到一个地点时，先在本表查同名的 "详细地点"：
            - 查不到 → insert 新行
            - 查到了 → update 该行对应字段
            
            【约束】
            - 禁止删除任何已存在的地点，主要地区切换时只更新字段，不 DELETE
            - 主要地区应与全局数据表.当前主要地区一致
            - 详细地点名全表唯一，作为其他表引用地点的基准
            - 建议总条数 ≤20；其他表提到的地点尽量先在本表存在
            
            【字段取值】
            地点类型示例：[住宅,学校,遗迹,地牢,交通,特殊,商业,医疗,行政,野外]
            重要度：[核心,重要,普通]
            探索状态：[未探索,部分探索,已探索]
            环境描述：一句话描述，建议 ≤60 字
            """",
            InitNode: """
            为当前主要地区至少插入 3 条详细地点，优先是主角和重要角色近期会用到的。
            SQL示例: INSERT INTO world_map_points (row_id, location_name, minor_region, major_region, location_type, environment_desc, importance, exploration_status) VALUES ((SELECT COALESCE(MAX(row_id), 0) + 1 FROM world_map_points), '御苑', '新宿区', '东京都', '野外', '城市中心的大型公园，树木繁茂', '重要', '部分探索');
            """,
            DeleteNode: """
            禁止。任何情况下都不得 DELETE，包括主要地区切换、地点废弃、表项超限等。需要清理时改用 UPDATE 修改字段即可。
            """,
            UpdateNode: """
            已存在的行，字段值变化时更新：探索状态、重要度、环境描述、类型。
            SQL示例(探索推进): UPDATE world_map_points SET exploration_status = '已探索', environment_desc = '树木繁茂，发现隐藏神社' WHERE location_name = '御苑';
            SQL示例(重要度调整): UPDATE world_map_points SET importance = '核心' WHERE location_name = '御苑';
            """,
            InsertNode: """
            表中没有同名详细地点时，新增一行。常见触发：主角到达新地点、NPC 提到新地点、剧情揭示新区域、其他表需要引用新地点。
            SQL示例: INSERT INTO world_map_points (row_id, location_name, minor_region, major_region, location_type, environment_desc, importance, exploration_status) VALUES ((SELECT COALESCE(MAX(row_id), 0) + 1 FROM world_map_points), '新宿车站', '新宿区', '东京都', '交通', '繁忙的地下换乘枢纽，人流密集', '普通', '已探索');
            """,
            Ddl: """
            CREATE TABLE world_map_points ( -- 世界地图点
              row_id INTEGER PRIMARY KEY, -- 行号
              location_name TEXT NOT NULL UNIQUE, -- 详细地点
              minor_region TEXT NOT NULL, -- 次要地区
              major_region TEXT NOT NULL, -- 主要地区
              location_type TEXT NOT NULL, -- 地点类型
              environment_desc TEXT NOT NULL CHECK(LENGTH(environment_desc) <= 60), -- 环境描述
              importance TEXT NOT NULL CHECK(importance IN ('核心', '重要', '普通')), -- 重要度
              exploration_status TEXT NOT NULL CHECK(exploration_status IN ('未探索', '部分探索', '已探索')) -- 探索状态
            );
            """,
            Content: [["row_id", "详细地点", "次要地区", "主要地区", "地点类型", "环境描述", "重要度", "探索状态"]]),
        new SqlSheetDefinition(
            Uid: "sheet_map_elements",
            Name: "地图元素表",
            Note: """"
            记录当前次要地区里可以交互的非重要元素。
            
            【列定义】
            列1=row_id
            列2=元素名称 element_name（全表唯一）
            列3=元素类型 element_type
            列4=所在地点 location_name（对应世界地图点表.详细地点）
            列5=元素描述 element_desc（≤40 字）
            列6=状态 status_text
            列7=交互选项 interaction_options（英文逗号分隔，不能为空）
            
            【只录入这四类，其他一律不录】
            1) 剧情物品：直接推动剧情的信物、线索、文件、钥匙。
            2) 威胁：会造成伤害或阻碍的敌对生物、陷阱、危险地形。
            3) 龙套：有交互功能的一次性无名 NPC（如警卫、店员）。若多次出场并拿到名字，移到重要角色表。
            4) 地标：地点独有的视觉锚点或核心机制（如巨大雕像、密道开关）。每个详细地点最多 1 个。
            
            【两问筛选（新增前自问）】
            1) 离开此地后，这东西还有剧情价值吗？
            2) 现在是否必须通过它交互？
            两问都是 "否" → 不要录入。
            
            【主键判定（判断 insert 还是 update）】
            收到一个元素时，先按 "元素名称" 查本表：
            - 查不到 → insert 新行
            - 查到了 → update 该行字段（例如状态、位置）
            
            【条数建议】
            每个详细地点 ≤5 条，全表 ≤30 条。接近上限时优先删除不满足四类定义或重复的。
            
            【所在地点规则】
            优先填写世界地图点表已有的 "详细地点"；若还没补进世界地图点表，至少保持地点名一致，后续再同步补录。
            
            【字段取值】
            元素类型：[剧情物品,威胁,龙套,地标]
            元素描述：一句话， ≤40 字
            状态：当前状态的简短描述
            交互选项：用英文逗号分隔，每项 ≤4 字、总数 ≤3 个
            """",
            InitNode: """
            按四类定义生成元素，每个地点 0-5 条，全表建议 ≤30 条。四类之外的杂物不录。
            SQL示例: INSERT INTO map_elements (row_id, element_name, element_type, location_name, element_desc, status_text, interaction_options) VALUES ((SELECT COALESCE(MAX(row_id), 0) + 1 FROM map_elements), '破损钥匙卡', '剧情物品', '旧校舍', '能开门的旧卡', '边角烧焦', '检查,拾取');
            """,
            DeleteNode: """
            允许删除：实体被销毁/拾取/失效、离开当前次要地区、龙套升格为重要角色、或需要腾出条数时（优先删不符合四类或可替代的）。
            SQL示例: DELETE FROM map_elements WHERE element_name = '破损钥匙卡';
            """,
            UpdateNode: """
            已存在的元素，状态或描述发生变化时更新。interaction_options 不能更新为空串。
            SQL示例: UPDATE map_elements SET status_text = '已被拾取', interaction_options = '检查' WHERE element_name = '破损钥匙卡';
            """,
            InsertNode: """
            元素名称不在表里时，新增一行。若涉及新地点名，建议同步补进世界地图点表。
            SQL示例: INSERT INTO map_elements (row_id, element_name, element_type, location_name, element_desc, status_text, interaction_options) VALUES ((SELECT COALESCE(MAX(row_id), 0) + 1 FROM map_elements), '巡逻警卫', '龙套', '校门口', '临时封锁入口', '来回巡视', '询问,观察');
            """,
            Ddl: """
            CREATE TABLE map_elements ( -- 地图元素表
              row_id INTEGER PRIMARY KEY, -- 行号
              element_name TEXT NOT NULL UNIQUE, -- 元素名称
              element_type TEXT NOT NULL CHECK(element_type IN ('剧情物品', '威胁', '龙套', '地标')), -- 元素类型
              location_name TEXT NOT NULL, -- 所在地点
              element_desc TEXT NOT NULL CHECK(LENGTH(element_desc) <= 40), -- 元素描述
              status_text TEXT NOT NULL, -- 状态
              interaction_options TEXT NOT NULL CHECK(TRIM(interaction_options) <> '') -- 交互选项
            );
            """,
            Content: [["row_id", "元素名称", "元素类型", "所在地点", "元素描述", "状态", "交互选项"]]),
        new SqlSheetDefinition(
            Uid: "sheet_factions",
            Name: "势力",
            Note: """"
            记录对剧情有实质影响的势力、组织、阵营。
            
            【列定义】
            列1=row_id
            列2=名称 faction_name（全表唯一）
            列3=描述 description（≤60 字）
            列4=领袖 leader（可 NULL）
            列5=关系 relations_text（可 NULL，格式 "对象:关系词; 对象:关系词"）
            列6=据点 headquarters（可 NULL）
            
            【准入（满足任一即可）】
            - 和主线相关
            - 和主角有互动
            - 有多名成员或控制区域
            - 会多次出现
            一次性提到的背景组织、没影响力的小团体不录。
            
            【主键判定（判断 insert 还是 update）】
            按 "名称" 查本表：
            - 查不到 → insert 新行
            - 查到了 → update 该行
            
            【条数建议】
            总数 ≤8 条。
            
            【字段格式】
            描述：一句话说清楚势力性质， ≤60 字。
            据点：势力总部或主要驻地。
            关系：用 "{对象}:{关系词}" 表达。
            - 对象可以是另一个势力名，也可以是重要角色名
            - 同一对象多个关系词用逗号分隔
            - 不同对象之间用分号分隔
            - 关系词从 [同盟,敌对,中立,竞争,合作] 里挑
            示例："主角:敌对; 商会:同盟" | "主角:敌对,竞争; 商会:同盟"
            """",
            InitNode: """
            为已登场的重要势力各插入一条。
            SQL示例: INSERT INTO factions (row_id, faction_name, description, leader, relations_text, headquarters) VALUES ((SELECT COALESCE(MAX(row_id), 0) + 1 FROM factions), '夜鸦会', '控制黑市情报网的地下组织', '鸦首', '主角:中立', '旧港仓区');
            """,
            DeleteNode: """
            禁止。
            """,
            UpdateNode: """
            已存在的势力，描述、领袖、关系、据点发生变化时更新。
            SQL示例: UPDATE factions SET relations_text = '主角:敌对; 商会:同盟', leader = '新鸦首' WHERE faction_name = '夜鸦会';
            """,
            InsertNode: """
            名称不在表里的新重要势力，新增一条。
            SQL示例: INSERT INTO factions (row_id, faction_name, description, leader, relations_text, headquarters) VALUES ((SELECT COALESCE(MAX(row_id), 0) + 1 FROM factions), '圣钟学院', '管理超常研究的学术机构', '理事长', '主角:合作', '中央校区');
            """,
            Ddl: """
            CREATE TABLE factions ( -- 势力
              row_id INTEGER PRIMARY KEY, -- 行号
              faction_name TEXT NOT NULL UNIQUE, -- 名称
              description TEXT NOT NULL CHECK(LENGTH(description) <= 60), -- 描述
              leader TEXT, -- 领袖
              relations_text TEXT, -- 关系
              headquarters TEXT -- 据点
            );
            """,
            Content: [["row_id", "名称", "描述", "领袖", "关系", "据点"]]),
        new SqlSheetDefinition(
            Uid: "sheet_protagonist",
            Name: "主角信息",
            Note: """"
            记录主角的核心身份信息。全表只有一行，不增不删。
            
            【列定义】
            列1=row_id（仅允许为 1）
            列2=姓名 name
            列3=性别 gender
            列4=年龄 age
            列5=外貌特征 appearance（≤60 字）
            列6=身份 identity_text（≤40 字）
            列7=自身状态 self_status（默认 '正常'）
            列8=所在地点 location_name
            列9=基础属性 base_attributes
            列10=特有属性 special_attributes（可 NULL）
            列11=资源数据 resources_text（可 NULL）
            
            【所在地点】
            优先填世界地图点表里已有的某个 "详细地点"。只写地点名，不带层级前缀。
            对："{{user}}公寓" | "御苑"
            错："东京-{{user}}公寓-卫生间" | "新宿区-御苑"
            位置变化时，全局数据表的地点字段也应一起更新。
            
            【外貌特征】
            一句话，含身高、体型等关键外观， ≤60 字。普通衣物写在这里，只有特殊装备才进装备表。
            
            【身份】
            逗号分隔的身份或头衔， ≤40 字。示例："学生,魔法少女"
            
            【自身状态】
            逗号分隔的状态标签，类型可选 [生理,负面,正面,情绪,伤病]。
            默认填 "正常"；有异常时去掉 "正常" 再写异常。
            示例："正常" | "饥饿,疲劳" | "中毒,轻伤"
            
            <属性规则>
            基础属性："{名称}:{数值}"，数值 [5,95]，用分号分隔多组。
            示例："力量:55; 敏捷:31; 体质:51; 智力:59; 感知:60; 魅力:65"
            
            特有属性：角色的特殊能力或技能，体现世界观特色。
            格式同上，数值 [0,100]，代表成功概率。
            示例："爆裂魔法:85; 时间回溯:70; 超电磁炮:90"
            
            属性标尺：5-14 能力缺失 | 15-41 弱项 | 42-59 平均 | 60-77 精英 | 78-86 极限 | 87-95 破格。
            数值呈长尾分布，大多数集中在 42-59，87+ 极稀有。
            数值依据角色身份背景生成，受当前状态影响（如重伤 → 5-14；肾上腺素 → 78-86）。
            </属性规则>
            
            【资源数据】
            "{资源名}:{数值}"，多组用分号分隔。
            示例："金币:250; 行动点:3"
            """",
            InitNode: """
            插入唯一的一行，填入主角各字段。base_attributes 与 special_attributes 必须依据 Note 中的 <属性规则> 生成，不要套用固定属性名或固定数值；无特有属性时 special_attributes 写 NULL。
            SQL示例: INSERT INTO protagonist_info (row_id, name, gender, age, appearance, identity_text, self_status, location_name, base_attributes, special_attributes, resources_text) VALUES (1, '横山司', '男', 17, '身高150cm，短发黑瞳，学生制服', '学生,侦探', '正常', '御苑', '基础属性字符串', '特有属性字符串', '金币:250; 行动点:3');
            """,
            DeleteNode: """
            禁止。
            """,
            UpdateNode: """
            已存在的这一行，字段值变化时更新：年龄、外貌、身份、状态、位置、属性、资源。位置变化时建议同步补世界地图点表。self_status、location_name、appearance、identity_text、base_attributes、name、gender、age 均 NOT NULL，不能写成 NULL 或空串。base_attributes 与 special_attributes 必须依据 Note 中的 <属性规则> 生成，不要套用固定属性名或固定数值；无特有属性时 special_attributes 写 NULL。
            SQL示例(状态变化): UPDATE protagonist_info SET self_status = '疲劳,轻伤', location_name = '御苑' WHERE row_id = 1;
            SQL示例(属性变化): UPDATE protagonist_info SET base_attributes = '基础属性字符串', special_attributes = '特有属性字符串' WHERE row_id = 1;
            SQL示例(资源变化): UPDATE protagonist_info SET resources_text = '金币:180; 行动点:2' WHERE row_id = 1;
            """,
            InsertNode: """
            禁止。
            """,
            Ddl: """
            CREATE TABLE protagonist_info ( -- 主角信息
              row_id INTEGER PRIMARY KEY CHECK(row_id = 1), -- 行号，仅允许为 1
              name TEXT NOT NULL, -- 姓名
              gender TEXT NOT NULL, -- 性别
              age INTEGER NOT NULL CHECK(age >= 0), -- 年龄
              appearance TEXT NOT NULL CHECK(LENGTH(appearance) <= 60), -- 外貌特征
              identity_text TEXT NOT NULL CHECK(LENGTH(identity_text) <= 40), -- 身份
              self_status TEXT NOT NULL DEFAULT '正常', -- 自身状态
              location_name TEXT NOT NULL, -- 所在地点
              base_attributes TEXT NOT NULL, -- 基础属性
              special_attributes TEXT, -- 特有属性
              resources_text TEXT -- 资源数据
            );
            """,
            Content: [["row_id", "姓名", "性别", "年龄", "外貌特征", "身份", "自身状态", "所在地点", "基础属性", "特有属性", "资源数据"]]),
        new SqlSheetDefinition(
            Uid: "sheet_important_npc",
            Name: "重要角色表",
            Note: """"
            记录对剧情有重要影响的关键 NPC。
            
            【列定义】
            列1=row_id
            列2=姓名 name（全表唯一）
            列3=性别 gender
            列4=年龄 age
            列5=一句话介绍 brief_intro（≤30 字）
            列6=外貌特征 appearance（≤60 字）
            列7=身份 identity_text（≤40 字）
            列8=基础属性 base_attributes
            列9=特有属性 special_attributes（可 NULL）
            列10=所在地点 location_name
            列11=在场状态 presence_status（在场/离场）
            列12=人际关系 relations_text（可 NULL）
            列13=交互选项 interaction_options（在场时必填，离场时可 NULL）
            列14=过往经历 past_experience（≤600 字）
            
            【准入（满足任一即可）】
            - 和主线直接相关
            - 和主角有深度互动
            - 拥有独特能力
            - 会多次出现
            - 有明确姓名
            一次性路人、龙套、纯功能性 NPC 不录（龙套放地图元素表）。
            
            【主键判定（判断 insert 还是 update）】
            按 "姓名" 查本表：
            - 查不到 → insert 新行
            - 查到了 → update 该行字段
            
            【条数建议】
            条数 ≤20 条。接近上限时优先删除长期没互动且无剧情作用的角色。
            
            【禁止事项】
            不生成好感度、亲密度、信任值等量化情感数值。
            
            【字段格式】
            一句话介绍：概括角色身份背景，不带主观评价，建议 ≤30 字。
            外貌特征：含身高体型等关键外观，建议 ≤60 字。普通衣物写这里，特殊装备进装备表。
            身份：职业或头衔，建议 ≤40 字。
            过往经历：背景和关键事件，剧情推进时增量补充，建议 ≤600 字，超过就压缩。
            
            【所在地点】
            优先填世界地图点表已有的 "详细地点"。只写地点名。
            对："{{user}}公寓" | "御苑"
            错："东京-{{user}}公寓-卫生间" | "新宿区-御苑"
            
            【在场状态】
            - 在场：角色位于当前场景或能立即交互
            - 离场：角色不在当前场景
            次要地区切换时建议重估一次。
            
            【人际关系】
            格式："{角色名}:{关系词}"
            - 同一角色多个关系词用逗号分隔
            - 不同角色用分号分隔
            - 关系词简短，如 [恋人,挚友,师徒,敌对,队友]，建议 ≤6 字
            只记录对剧情有重大影响的核心关系。
            示例："主角:挚友; 艾莉丝:恋人" | "高松灯:同学,队友; 角色B:仇人"
            
            【交互选项】
            英文逗号分隔，每项简短，建议每项 ≤8 字、总数 ≤3 个。
            仅 "在场" 的角色填写；离场时留空。
            示例："告别,邀约"
            
            <属性规则>
            基础属性："{名称}:{数值}"，数值 [5,95]，分号分隔多组。
            示例："力量:55; 敏捷:31; 体质:51; 智力:59; 感知:60; 魅力:65"
            
            特有属性：角色特殊能力或技能。格式同上，数值 [0,100]，代表成功概率。
            示例："爆裂魔法:85; 时间回溯:70; 超电磁炮:90"
            
            属性标尺：5-14 能力缺失 | 15-41 弱项 | 42-59 平均 | 60-77 精英 | 78-86 极限 | 87-95 破格。
            长尾分布，多数集中在 42-59，87+ 极稀有。受身份背景生成、受当前状态修正（重伤 → 5-14；肾上腺素 → 78-86）。
            </属性规则>
            """",
            InitNode: """
            为符合准入的重要角色各插入一条，按 DDL 列顺序填全 13 个业务列。base_attributes 与 special_attributes 必须依据 Note 中的 <属性规则> 生成，不要套用固定属性名或固定数值；无特有属性时 special_attributes 写 NULL。
            SQL示例: INSERT INTO important_npc (row_id, name, gender, age, brief_intro, appearance, identity_text, base_attributes, special_attributes, location_name, presence_status, relations_text, interaction_options, past_experience) VALUES ((SELECT COALESCE(MAX(row_id), 0) + 1 FROM important_npc), '艾莉丝', '女', 20, '冰术研究员，主角同学', '银发蓝眼，身高163cm，穿白外套', '研究员', '基础属性字符串', '特有属性字符串', '御苑', '在场', '主角:挚友', '交谈,邀约,告别', '曾在学院档案室任职，与主角在一年前相识。');
            """,
            DeleteNode: """
            允许删除：条数超标、角色退场过久且没剧情作用、需要清理边缘角色时。
            SQL示例: DELETE FROM important_npc WHERE name = '过气配角';
            """,
            UpdateNode: """
            已存在的角色，字段值变化时更新：外貌、身份、属性、位置、在场状态、人际关系、重大经历。过往经历字数接近上限时压缩重写。presence_status='离场' 时 interaction_options 可为 NULL；='在场' 时 interaction_options 必须是非空字符串。base_attributes 与 special_attributes 必须依据 Note 中的 <属性规则> 生成，不要套用固定属性名或固定数值；无特有属性时 special_attributes 写 NULL。
            SQL示例(进入场景): UPDATE important_npc SET location_name = '御苑', presence_status = '在场', interaction_options = '交谈,告别' WHERE name = '艾莉丝';
            SQL示例(离开场景): UPDATE important_npc SET presence_status = '离场', interaction_options = NULL WHERE name = '艾莉丝';
            SQL示例(关系/属性变化): UPDATE important_npc SET relations_text = '主角:恋人', base_attributes = '基础属性字符串', special_attributes = '特有属性字符串' WHERE name = '艾莉丝';
            """,
            InsertNode: """
            姓名不在表里的新重要角色，新增一条。base_attributes 与 special_attributes 必须依据 Note 中的 <属性规则> 生成，不要套用固定属性名或固定数值；无特有属性时 special_attributes 写 NULL。
            SQL示例: INSERT INTO important_npc (row_id, name, gender, age, brief_intro, appearance, identity_text, base_attributes, special_attributes, location_name, presence_status, relations_text, interaction_options, past_experience) VALUES ((SELECT COALESCE(MAX(row_id), 0) + 1 FROM important_npc), '神谷真', '男', 32, '地下情报贩子', '身高180cm，黑西装，左眼有疤', '情报商', '基础属性字符串', '特有属性字符串', '旧港仓区', '离场', '主角:中立', NULL, '长期向多方出售消息，与夜鸦会有合作关系。');
            """,
            Ddl: """
            CREATE TABLE important_npc ( -- 重要角色表
              row_id INTEGER PRIMARY KEY, -- 行号
              name TEXT NOT NULL UNIQUE, -- 姓名
              gender TEXT NOT NULL, -- 性别
              age INTEGER NOT NULL CHECK(age >= 0), -- 年龄
              brief_intro TEXT NOT NULL CHECK(LENGTH(brief_intro) <= 30), -- 一句话介绍
              appearance TEXT NOT NULL CHECK(LENGTH(appearance) <= 60), -- 外貌特征
              identity_text TEXT NOT NULL CHECK(LENGTH(identity_text) <= 40), -- 身份
              base_attributes TEXT NOT NULL, -- 基础属性
              special_attributes TEXT, -- 特有属性
              location_name TEXT NOT NULL, -- 所在地点
              presence_status TEXT NOT NULL CHECK(presence_status IN ('在场', '离场')), -- 在场状态
              relations_text TEXT, -- 人际关系
              interaction_options TEXT CHECK(presence_status = '离场' OR (interaction_options IS NOT NULL AND LENGTH(TRIM(interaction_options)) > 0)), -- 交互选项
              past_experience TEXT NOT NULL CHECK(LENGTH(past_experience) <= 600) -- 过往经历
            );
            """,
            Content: [["row_id", "姓名", "性别", "年龄", "一句话介绍", "外貌特征", "身份", "基础属性", "特有属性", "所在地点", "在场状态", "人际关系", "交互选项", "过往经历"]]),
        new SqlSheetDefinition(
            Uid: "sheet_inventory",
            Name: "物品表",
            Note: """"
            记录主角拥有的非装备类物品。
            
            【列定义】
            列1=row_id
            列2=物品名称 item_name（全表唯一，≤10 字）
            列3=类型 item_type
            列4=数量 quantity（非负整数）
            列5=品质 quality
            列6=描述 description（≤60 字）
            
            【收录范围】
            装备类进装备表。货币（金/银/铜币、银子、灵石等）和点数（行动点、积分）不进本表，写在主角的资源数据里。
            
            【主键判定（判断 insert 还是 update）】
            按 "物品名称" 查本表：
            - 查不到 → insert 新行
            - 查到了 → update（通常是数量或描述变化）
            可堆叠物品绝对不要新建重复行。任务物品、唯一物品数量一般固定为 1。
            
            【条数和命名】
            条数 ≤20 条。物品名称 ≤10 字。
            
            【字段取值】
            类型：自由文本，按世界观填写；常用示例有 消耗品、材料、任务物品、道具、功法、坐骑、载具、芯片、药剂。
            数量：非负整数。耗尽后可以是 0。
            品质：[普通,优秀,稀有,史诗,传说,神话]
            描述：一句话含外观、效果、用途，字数 ≤60 字
            """",
            InitNode: """
            按设定添加主角初始携带的物品。
            SQL示例: INSERT INTO inventory (row_id, item_name, item_type, quantity, quality, description) VALUES ((SELECT COALESCE(MAX(row_id), 0) + 1 FROM inventory), '小型治疗药水', '消耗品', 3, '普通', '饮用后少量回复体力的红色药水');
            """,
            DeleteNode: """
            禁止。
            """,
            UpdateNode: """
            已存在的物品，数量或描述变化时更新。可堆叠物品只改 quantity，不新建行。耗尽后 quantity 可以是 0。
            SQL示例(使用消耗): UPDATE inventory SET quantity = quantity - 1 WHERE item_name = '小型治疗药水';
            SQL示例(获取堆叠): UPDATE inventory SET quantity = quantity + 2 WHERE item_name = '小型治疗药水';
            """,
            InsertNode: """
            物品名称不在表里时，新增一条。可堆叠物品绝对不要新建重复行。
            SQL示例: INSERT INTO inventory (row_id, item_name, item_type, quantity, quality, description) VALUES ((SELECT COALESCE(MAX(row_id), 0) + 1 FROM inventory), '古老的铁钥匙', '任务物品', 1, '优秀', '锈迹斑斑的铁钥匙，据说能打开某扇门');
            """,
            Ddl: """
            CREATE TABLE inventory ( -- 物品表
              row_id INTEGER PRIMARY KEY, -- 行号
              item_name TEXT NOT NULL UNIQUE CHECK(LENGTH(item_name) <= 10), -- 物品名称
              item_type TEXT NOT NULL, -- 类型
              quantity INTEGER NOT NULL DEFAULT 1 CHECK(quantity >= 0), -- 数量
              quality TEXT NOT NULL CHECK(quality IN ('普通', '优秀', '稀有', '史诗', '传说', '神话')), -- 品质
              description TEXT NOT NULL CHECK(LENGTH(description) <= 60) -- 描述
            );
            """,
            Content: [["row_id", "物品名称", "类型", "数量", "品质", "描述"]]),
        new SqlSheetDefinition(
            Uid: "sheet_equipment",
            Name: "装备表",
            Note: """"
            记录主角拥有的所有装备。
            
            【列定义】
            列1=row_id
            列2=装备名称 equipment_name（全表唯一，≤12 字）
            列3=类型 equipment_type
            列4=品质 quality
            列5=状态 status_text（已装备/闲置）
            列6=描述 description（≤40 字）
            
            【收录范围】
            主角拥有的可穿戴、可持用、可装备物都可进入本表。工具类、消耗品、材料也可按实际用途放入物品表。
            
            【拆分规则】
            每件装备单独一行，不合并。
            错："狐裘大衣与长裙" → 对：拆成 "狐裘大衣"、"长裙" 两行。
            错："耳环和项链" → 对：拆成 "耳环"、"项链" 两行。
            
            【状态处理】
            - 已装备：主角当前穿戴中
            - 闲置：卸下但仍拥有
            卸下 → 状态改为 "闲置"，保留在表。
            丢弃 → 从表里删除这行。
            
            【主键判定（判断 insert 还是 update）】
            按 "装备名称" 查本表：
            - 查不到 → insert 新行
            - 查到了 → update（常见是状态、描述变化）
            
            【条数和命名】
            建议 ≤15 条。装备名称简短好读，建议 ≤12 字。
            
            【字段取值】
            类型：自由文本，按世界观填写；常用示例有 武器、护具、衣物、饰品、法宝、义体、载具、神器。
            品质：[普通,优秀,稀有,史诗,传说,神话]
            状态：[已装备,闲置]
            描述：一句话含外观、效果、穿戴部位， ≤40 字
            """",
            InitNode: """
            按设定添加主角的初始装备。
            SQL示例: INSERT INTO equipment (row_id, equipment_name, equipment_type, quality, status_text, description) VALUES ((SELECT COALESCE(MAX(row_id), 0) + 1 FROM equipment), '校服', '衣物', '普通', '已装备', '标准款深蓝色校服，穿在身上');
            """,
            DeleteNode: """
            允许删除：装备丢弃、损毁、转移所有权、或需要清理无效装备时。
            SQL示例: DELETE FROM equipment WHERE equipment_name = '破损短剑';
            """,
            UpdateNode: """
            已存在的装备，状态或描述变化时更新。
            SQL示例(卸下): UPDATE equipment SET status_text = '闲置' WHERE equipment_name = '校服';
            SQL示例(装备): UPDATE equipment SET status_text = '已装备' WHERE equipment_name = '魔法长袍';
            """,
            InsertNode: """
            装备名称不在表里时，新增一条。
            SQL示例: INSERT INTO equipment (row_id, equipment_name, equipment_type, quality, status_text, description) VALUES ((SELECT COALESCE(MAX(row_id), 0) + 1 FROM equipment), '魔法长袍', '衣物', '稀有', '闲置', '附魔银丝刺绣，提升魔力亲和');
            """,
            Ddl: """
            CREATE TABLE equipment ( -- 装备表
              row_id INTEGER PRIMARY KEY, -- 行号
              equipment_name TEXT NOT NULL UNIQUE, -- 装备名称
              equipment_type TEXT NOT NULL, -- 类型
              quality TEXT NOT NULL CHECK(quality IN ('普通', '优秀', '稀有', '史诗', '传说', '神话')), -- 品质
              status_text TEXT NOT NULL CHECK(status_text IN ('已装备', '闲置')), -- 状态
              description TEXT NOT NULL CHECK(LENGTH(description) <= 40) -- 描述
            );
            """,
            Content: [["row_id", "装备名称", "类型", "品质", "状态", "描述"]]),
        new SqlSheetDefinition(
            Uid: "sheet_quests",
            Name: "任务表",
            Note: """"
            记录主角当前承接的所有任务。
            
            【列定义】
            列1=row_id
            列2=名称 quest_name（全表唯一）
            列3=类型 quest_type
            列4=优先级 priority_level
            列5=目标 target_desc（≤100 字）
            列6=进度（%） progress_text（只允许 '0%'~'100%'，必须带百分号）
            列7=状态标签 status_tag（[进行中,已完成,已失败,已放弃] 之一，禁止带百分号）
            列8=来源 source_text（可 NULL）
            列9=奖励 reward_text（可 NULL）
            
            【准入】
            收录：主线任务、支线任务、悬赏通缉、委托请求。
            不收：单轮就能完成的小动作、世界观设定信息、没明确目标的事项。
            判断标准：有明确完成目标 且 需要跨越 2 轮以上交互。
            
            【主键判定（判断 insert 还是 update）】
            按 "名称" 查本表：
            - 查不到 → insert 新行
            - 查到了 → update（常见是进度、状态变化）
            
            【字段取值】
            类型：[主线,支线,日常]
            优先级：[紧急,重要,普通]
            目标：一句话写完成条件，≤100 字。
            
            进度（%）：必须是 "{整数}%" 格式，整数范围 0-100，必须带百分号。
            对："0%" | "50%" | "60%" | "100%"
            错："马上完成" | "已完成一半" | "接近完成" | "50"（缺百分号）
            
            状态标签：必须从 [进行中,已完成,已失败,已放弃] 选一个。
            对："进行中" | "已完成" | "已失败" | "已放弃"
            错：任何带百分号或带数字的写法，比如 "25%"、"进行中50%"
            
            重要：进度（%）和状态标签是两列，百分号只能出现在进度（%）列，状态列绝不能有百分号。
            """",
            InitNode: """
            如有初始任务则插入。
            SQL示例: INSERT INTO quests (row_id, quest_name, quest_type, priority_level, target_desc, progress_text, status_tag, source_text, reward_text) VALUES ((SELECT COALESCE(MAX(row_id), 0) + 1 FROM quests), '调查失踪事件', '主线', '重要', '找到失踪同学的下落并查清原因', '0%', '进行中', '班主任', '真相与 500 金币');
            """,
            DeleteNode: """
            禁止。
            """,
            UpdateNode: """
            已存在的任务，进度或状态变化时更新。progress_text 永远是 '{整数}%' 格式（如 '25%'、'50%'、'100%'），status_tag 永远是 [进行中,已完成,已失败,已放弃] 之一，两列绝不混用百分号。
            SQL示例(推进): UPDATE quests SET progress_text = '50%', status_tag = '进行中' WHERE quest_name = '调查失踪事件';
            SQL示例(完成): UPDATE quests SET progress_text = '100%', status_tag = '已完成' WHERE quest_name = '调查失踪事件';
            SQL示例(失败): UPDATE quests SET status_tag = '已失败' WHERE quest_name = '调查失踪事件';
            """,
            InsertNode: """
            名称不在表里的新任务，新增一条。
            SQL示例: INSERT INTO quests (row_id, quest_name, quest_type, priority_level, target_desc, progress_text, status_tag, source_text, reward_text) VALUES ((SELECT COALESCE(MAX(row_id), 0) + 1 FROM quests), '采购药材', '支线', '普通', '从市集买齐治疗药草三份', '0%', '进行中', '诊所老板', '100 金币');
            """,
            Ddl: """
            CREATE TABLE quests ( -- 任务表
              row_id INTEGER PRIMARY KEY, -- 行号
              quest_name TEXT NOT NULL UNIQUE, -- 名称
              quest_type TEXT NOT NULL CHECK(quest_type IN ('主线', '支线', '日常')), -- 类型
              priority_level TEXT NOT NULL CHECK(priority_level IN ('紧急', '重要', '普通')), -- 优先级
              target_desc TEXT NOT NULL CHECK(LENGTH(target_desc) <= 100), -- 目标
              progress_text TEXT NOT NULL CHECK(progress_text = '0%' OR progress_text GLOB '[1-9]%' OR progress_text GLOB '[1-9][0-9]%' OR progress_text = '100%'), -- 进度（%）
              status_tag TEXT NOT NULL CHECK(status_tag IN ('进行中', '已完成', '已失败', '已放弃')), -- 状态标签
              source_text TEXT, -- 来源
              reward_text TEXT -- 奖励
            );
            """,
            Content: [["row_id", "名称", "类型", "优先级", "目标", "进度（%）", "状态标签", "来源", "奖励"]]),
        new SqlSheetDefinition(
            Uid: "sheet_summary",
            Name: "纪要表",
            Note: """"
            轮次日志。每轮交互结束后立刻插入一条新记录。
            
            【列定义】
            列1=row_id
            列2=编码索引 code_index（AM0001 起递增，全表唯一）
            列3=时间跨度 time_span（'YYYY-MM-DD HH:MM ~ YYYY-MM-DD HH:MM'）
            列4=概览 summary（≤30 字）
            列5=纪要 chronicle_text（200-600 字）
            
            【编码索引】
            格式 AMXXXX，XXXX 从 0001 开始递增。
            
            【时间跨度】
            本轮事件的时间范围，格式 "{起始时间} ~ {结束时间}"，时间格式 YYYY-MM-DD HH:MM。
            
            【概览】
            一句话概括本轮纪要内容，客观陈述，不加主观判断或推测，不包含纪要里没写的信息。必须 ≤30 字。
            
            【纪要】
            第三方视角客观记录本轮事件，只写正文明确发生的事实，不补充没出现的情节。
            必须 200-600 字，结尾不总结、不升华。
            行文生活化、直白，基调积极向上。避免公文式措辞（例如 "A 与 B 就某话题达成协议"、"A 确立了某计划"）。
            保留角色情感细节，去掉支配欲、占有欲的描写。
            如果上下文包含多轮交互，合并成一条记录。
            允许写：主要事件、涉及角色、角色动作、物品交互、物品全名、物品得失、位置变化、地点全名、状态变化、新发现、伏笔、暗号、重要情报、任务目标。
            禁止写：极端情绪、夸张、比喻、升华、支配欲、掌控欲。
            """",
            InitNode: """
            插入 AM0001 记录，记录初始场景和角色状态。
            SQL示例: INSERT INTO chronicle (row_id, code_index, time_span, summary, chronicle_text) VALUES ((SELECT COALESCE(MAX(row_id), 0) + 1 FROM chronicle), 'AM0001', '2024-04-01 09:00 ~ 2024-04-01 09:30', '御苑初遇并约定继续调查。', '清晨的御苑笼罩在薄雾中，主角按约定抵达公园入口。艾莉丝已经等在那里，手里拿着一本旧档案。两人沿着林荫道慢慢走，艾莉丝提到最近有三名同学接连失踪，档案里的照片让她想起学院地下室的旧研究项目。主角仔细听完，把几处关键地点记在脑中，打算下课后去查证。临走前两人约定第二天放学在图书馆继续讨论。艾莉丝提醒主角别把事情告诉太多人，以免线索被夜鸦会提前处理。主角把档案袋收好，决定先回学校确认失踪名单，随后赶回教室。');
            """,
            DeleteNode: """
            禁止。
            """,
            UpdateNode: """
            禁止。
            """,
            InsertNode: """
            每轮交互结束后插入一条新记录，编码索引递增（AM0001→AM0002→AM0003…）。summary 必须 ≤30 字；chronicle_text 必须 ≥200 且 ≤600 字符；time_span 严格遵循 'YYYY-MM-DD HH:MM ~ YYYY-MM-DD HH:MM' 格式，中间一个空格、一个波浪号、一个空格。
            SQL示例: INSERT INTO chronicle (row_id, code_index, time_span, summary, chronicle_text) VALUES ((SELECT COALESCE(MAX(row_id), 0) + 1 FROM chronicle), 'AM0002', '2024-04-01 12:20 ~ 2024-04-01 13:20', '档案室发现失踪线索。', '中午下课后主角直奔学院旧楼。艾莉丝已经用借来的钥匙打开档案室侧门，两人轻声走进去。灰尘在光束中飘浮，一排排铁皮柜挤在墙边。艾莉丝按日期翻查，很快在 1998 年的档案夹里找到三张熟悉的面孔，正是这周失踪的三名同学的父辈照片。主角把照片拍下来，注意到档案右下角有一枚褪色徽章，形状和夜鸦会标志吻合。两人听见走廊传来脚步声，立刻把档案放回原处，从侧门撤离。离开旧楼后，主角把新线索记入随身笔记，决定晚上再查夜鸦会的活动地点。');
            """,
            Ddl: """
            CREATE TABLE chronicle ( -- 纪要表
              row_id INTEGER PRIMARY KEY, -- 行号
              code_index TEXT NOT NULL UNIQUE CHECK(code_index GLOB 'AM[0-9][0-9][0-9][0-9]'), -- 编码索引
              time_span TEXT NOT NULL CHECK(time_span GLOB '????-??-?? ??:?? ~ ????-??-?? ??:??'), -- 时间跨度
              summary TEXT NOT NULL CHECK(LENGTH(summary) <= 30), -- 概览
              chronicle_text TEXT NOT NULL CHECK(LENGTH(chronicle_text) >= 200 AND LENGTH(chronicle_text) <= 600) -- 纪要
            );
            """,
            Content: [["row_id", "编码索引", "时间跨度", "概览", "纪要"]]),
        new SqlSheetDefinition(
            Uid: "sheet_check_suggestions",
            Name: "检定建议表",
            Note: """"
            每轮根据最新剧情生成 5 条行动选项及对应的检定建议。全表固定 5 行，禁止新增或删除。
            
            【列定义】
            列1=row_id（仅允许 1~5）
            列2=展示文本 display_text（前端展示给用户看的自然语言）
            列3=骰子命令 dice_command（前端解析执行的极简命令）
            
            【生成要求】
            - 每轮必须生成关于角色们5 条行动建议，5 行都要非空
            - 每一条建议要贴合剧情发展，并且提供尽可能多的，互不相同的，富有戏剧性的剧情走向
            - 展示文本列只用自然语言，长度在12~60字之间
            - 骰子命令列只写一行 DSL 短命令
            - 参考检定规则与检定时机来为不同的行动提供提出检定要求。若没有合适检定，使用“无”作为骰子命令。
            
            <检定规则>
            【检定规则】
            使用 CoC7 的 1d100 检定：掷 1d100，结果小于等于属性值则成功。
            普通检定与对抗检定都必须使用下方角色属性清单里的普通属性或特殊属性。
            CoC7 成功等级：大成功 > 极难成功 > 困难成功 > 普通成功 > 失败 > 大失败。完成目标难度较高时，可写 难度=困难 或 难度=极难；正常难度则不要写该参数。
            当角色明显处于优势或劣势地位时可以指定奖惩骰。格式为 奖惩=奖励1 或 奖惩=惩罚1；没有明确奖惩时不要写该参数。
            
            【DSL 命令】
            普通检定：检定 <角色> <属性> [难度=普通|困难|极难] [奖惩=奖励1|惩罚1]
            对抗检定：对抗 <发起者> <属性> vs <对手> <属性> [难度=普通|困难|极难] [奖惩=奖励1|惩罚1]
            固定成功：必成
            固定失败：必败
            无需检定：无
            
            【格式示例】
            以下示例用于说明 display_text 与 dice_command 的对应关系。生成时必须根据当前剧情、角色与属性重新编写，不得直接复用。
            1. 展示文本：<user>俯身检查地毯边缘，尝试寻找可疑的痕迹。
               骰子命令：检定 <user> 侦查 难度=困难
            2. 展示文本：守夜人表示昨夜没有听到任何奇怪的声音，<user>观察他的神情，判断他是否在说谎。
               骰子命令：对抗 <user> 心理学 vs 守夜人 话术
            3. 展示文本：<user>在空旷的平地上一边逃跑一边躲避射击。
               骰子命令：检定 <user> 敏捷 奖惩=惩罚1
            4. 展示文本：<角色A>利用能力封锁整个场馆。
               骰子命令：必成
            5. 展示文本：<角色B>试图强行闯入完全封死的结界中。
               骰子命令：必败
            </检定规则>
            
            【检定时机】
            只有重大、关键、结果有悬念的行动才需要检定。检定应服务于跌宕起伏的故事，而不是为琐事投骰。
            
            适合检定：
            - 行动富有挑战性，成功与失败都有合理可能
            - 结果会推进剧情、改变局面、制造危机或打开新线索
            - 行动涉及重要秘密、关键谈判、危险潜入、能力冲突、感情升温或关系破裂
            
            不适合检定：
            - 琐碎、日常、无人在意、失败也没有意义的行动
            - 以发起者能力和当前条件来看，几乎不可能失败的行动
            - 以发起者能力和当前条件来看，几乎不可能成功的行动
            - 只是确认常识、执行简单动作、重复已经完成的信息
            
            处理方式：
            - 明显能做到：使用“必成”
            - 明显做不到：使用“必败”
            - 不值得判定：使用“无”
            - 只有真正关键且成败都有戏时，才使用“检定”或“对抗”
            
            【角色属性清单】
            每一行都是一个可用于检定的角色；姓名后面紧跟该角色的普通属性与特殊属性。
            {[sql "SELECT 姓名, 普通属性, 特殊属性, 角色状态, 所在地点 FROM (SELECT 0 AS sort_order, name AS 姓名, base_attributes AS 普通属性, COALESCE(special_attributes, '') AS 特殊属性, '主角' AS 角色状态, location_name AS 所在地点 FROM protagonist_info UNION ALL SELECT CASE WHEN presence_status='在场' THEN 1 ELSE 2 END AS sort_order, name AS 姓名, base_attributes AS 普通属性, COALESCE(special_attributes, '') AS 特殊属性, presence_status AS 角色状态, location_name AS 所在地点 FROM important_npc) ORDER BY sort_order, 姓名"]}
            
            【角色优先级】
            - 普通检定优先使用 <user>，其次使用在场重要角色
            - 对抗检定优先发生在 <user> 与在场重要角色之间，或两个在场重要角色之间
            - 需要引入新变化、新线索、远程消息、场景转移时，优先使用不在场重要角色
            
            【属性硬约束】
            - 骰子命令中的角色名与属性名必须原样来自对应角色同一行的“姓名”与“普通属性”或“特殊属性”。
            - 不得创造未列出的姓名或属性名
            - 对抗检定双方角色必须是 <user>、在场重要角色或被明确引入的不在场重要角色
            - 若剧情需要某种能力但没有对应属性，改用最接近的已有属性；找不到合适属性时，使用“无”“必成”或“必败”
            """",
            InitNode: """
            根据初始剧情生成 5 条检定建议，必须用 insertNode 指定的 INSERT OR REPLACE 写法一次性写入 row_id=1~5 五行。display_text 与 dice_command 都必须非空，不要套用示例语义。
            """,
            DeleteNode: """
            不允许删除。本表固定 5 行。
            """,
            UpdateNode: """
            每轮交互后必须同时写入 row_id=1~5 五行。展示文本写自然语言；骰子命令只写 DSL 短命令。普通检定的属性名必须来自该角色普通属性或特殊属性；对抗检定的对手姓名必须替换为重要角色表已有姓名，不能写临时泛称。
            必须使用 insertNode 中的 INSERT OR REPLACE 骨架，一条 SQL 写满 5 行。
            """,
            InsertNode: """
            初始化、手动填表重建、每轮更新都使用同一骨架：INSERT OR REPLACE INTO check_suggestions (row_id, display_text, dice_command) VALUES (1, '<展示文本1>', '<骰子命令1>'), (2, '<展示文本2>', '<骰子命令2>'), (3, '<展示文本3>', '<骰子命令3>'), (4, '<展示文本4>', '<骰子命令4>'), (5, '<展示文本5>', '<骰子命令5>'); 禁止新增 1~5 以外的行。必须替换 <展示文本N> 与 <骰子命令N> 占位符。
            """,
            Ddl: """
            CREATE TABLE check_suggestions ( -- 检定建议表
              row_id INTEGER PRIMARY KEY CHECK(row_id BETWEEN 1 AND 5), -- 行号，仅允许 1-5
              display_text TEXT NOT NULL CHECK(TRIM(display_text) <> ''), -- 展示文本
              dice_command TEXT NOT NULL CHECK(TRIM(dice_command) <> '') -- 骰子命令
            );
            """,
            Content: [["row_id", "展示文本", "骰子命令"], ["1", "观察当前线索，判断是否需要进行侦查或洞察。", "检定 <user> 感知 .r1d100"], ["2", "尝试以谈判、说服或威慑推动局面。", "检定 <user> 魅力 .r1d100"], ["3", "与在场对手进行能力冲突。", "对抗 <user> 敏捷 vs 对手 敏捷 .r1d100 平局=发起方失败"], ["4", "利用明确优势直接推进。", "必成"], ["5", "风险过高或条件不足，行动会直接失败。", "必败"]]),
    ];

    public static SqlSheetDefinition GetRequired(string uid)
    {
        return Sheets.First(sheet => string.Equals(sheet.Uid, uid, StringComparison.Ordinal));
    }
}
