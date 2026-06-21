namespace Re0Agent.Core.Database;

public static class DatabaseSchema
{
    public static readonly string[] ExpectedTableNames =
    [
        "global_state",
        "world_map_points",
        "map_elements",
        "factions",
        "protagonist_info",
        "important_npc",
        "inventory",
        "equipment",
        "quests",
        "chronicle",
        "character_memory",
        "save_points",
        "death_return_log",
        "agent_config",
        "protagonist_templates",
        "api_routing",
        "chat_sessions"
    ];

    public static readonly string[] CreateStatements =
    [
        """
        CREATE TABLE IF NOT EXISTS global_state (
          row_id INTEGER PRIMARY KEY CHECK(row_id = 1),
          current_location TEXT NOT NULL,
          current_minor_region TEXT NOT NULL,
          current_major_region TEXT NOT NULL,
          prev_scene_time TEXT CHECK(prev_scene_time IS NULL OR prev_scene_time GLOB '????-??-?? ??:??'),
          elapsed_time TEXT NOT NULL,
          cur_time TEXT NOT NULL CHECK(cur_time GLOB '????-??-?? ??:??'),
          current_chapter INTEGER NOT NULL DEFAULT 1 CHECK(current_chapter >= 1),
          is_lewd TEXT NOT NULL DEFAULT '否' CHECK(is_lewd IN ('是', '否'))
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS world_map_points (
          row_id INTEGER PRIMARY KEY,
          location_name TEXT NOT NULL UNIQUE,
          minor_region TEXT NOT NULL,
          major_region TEXT NOT NULL,
          location_type TEXT NOT NULL,
          environment_desc TEXT NOT NULL CHECK(LENGTH(environment_desc) <= 60),
          importance TEXT NOT NULL CHECK(importance IN ('核心', '重要', '普通')),
          exploration_status TEXT NOT NULL CHECK(exploration_status IN ('未探索', '部分探索', '已探索'))
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS map_elements (
          row_id INTEGER PRIMARY KEY,
          element_name TEXT NOT NULL UNIQUE,
          element_type TEXT NOT NULL CHECK(element_type IN ('剧情物品', '威胁', '龙套', '地标')),
          location_name TEXT NOT NULL,
          element_desc TEXT NOT NULL CHECK(LENGTH(element_desc) <= 40),
          status_text TEXT NOT NULL,
          interaction_options TEXT NOT NULL CHECK(TRIM(interaction_options) <> '')
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS factions (
          row_id INTEGER PRIMARY KEY,
          faction_name TEXT NOT NULL UNIQUE,
          description TEXT NOT NULL CHECK(LENGTH(description) <= 60),
          leader TEXT,
          relations_text TEXT,
          headquarters TEXT
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS protagonist_info (
          row_id INTEGER PRIMARY KEY CHECK(row_id = 1),
          char_id INTEGER NOT NULL DEFAULT 0,
          name TEXT NOT NULL,
          gender TEXT NOT NULL,
          age INTEGER NOT NULL CHECK(age >= 0),
          appearance TEXT NOT NULL CHECK(LENGTH(appearance) <= 60),
          identity_text TEXT NOT NULL CHECK(LENGTH(identity_text) <= 40),
          self_status TEXT NOT NULL DEFAULT '正常',
          location_name TEXT NOT NULL,
          base_attributes TEXT NOT NULL,
          special_attributes TEXT,
          resources_text TEXT,
          hp INTEGER NOT NULL DEFAULT 100,
          max_hp INTEGER NOT NULL DEFAULT 100,
          mp INTEGER NOT NULL DEFAULT 0,
          max_mp INTEGER NOT NULL DEFAULT 0,
          stamina INTEGER NOT NULL DEFAULT 100,
          max_stamina INTEGER NOT NULL DEFAULT 100,
          armor INTEGER NOT NULL DEFAULT 0,
          skills_json TEXT
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS important_npc (
          row_id INTEGER PRIMARY KEY,
          char_id INTEGER NOT NULL DEFAULT 0,
          name TEXT NOT NULL UNIQUE,
          gender TEXT NOT NULL,
          age INTEGER NOT NULL CHECK(age >= 0),
          brief_intro TEXT NOT NULL CHECK(LENGTH(brief_intro) <= 30),
          appearance TEXT NOT NULL CHECK(LENGTH(appearance) <= 60),
          identity_text TEXT NOT NULL CHECK(LENGTH(identity_text) <= 40),
          base_attributes TEXT NOT NULL,
          special_attributes TEXT,
          location_name TEXT NOT NULL,
          relations_text TEXT,
          interaction_options TEXT,
          past_experience TEXT NOT NULL CHECK(LENGTH(past_experience) <= 600),
          self_status TEXT NOT NULL DEFAULT '正常',
          hp INTEGER NOT NULL DEFAULT 100,
          max_hp INTEGER NOT NULL DEFAULT 100,
          mp INTEGER NOT NULL DEFAULT 0,
          max_mp INTEGER NOT NULL DEFAULT 0,
          stamina INTEGER NOT NULL DEFAULT 100,
          max_stamina INTEGER NOT NULL DEFAULT 100,
          armor INTEGER NOT NULL DEFAULT 0,
          skills_json TEXT
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS inventory (
          row_id INTEGER PRIMARY KEY,
          item_name TEXT NOT NULL UNIQUE CHECK(LENGTH(item_name) <= 10),
          item_type TEXT NOT NULL,
          quantity INTEGER NOT NULL DEFAULT 1 CHECK(quantity >= 0),
          quality TEXT NOT NULL CHECK(quality IN ('普通', '优秀', '稀有', '史诗', '传说', '神话')),
          description TEXT NOT NULL CHECK(LENGTH(description) <= 60)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS equipment (
          row_id INTEGER PRIMARY KEY,
          equipment_name TEXT NOT NULL UNIQUE,
          equipment_type TEXT NOT NULL,
          quality TEXT NOT NULL CHECK(quality IN ('普通', '优秀', '稀有', '史诗', '传说', '神话')),
          status_text TEXT NOT NULL CHECK(status_text IN ('已装备', '闲置')),
          description TEXT NOT NULL CHECK(LENGTH(description) <= 40)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS quests (
          row_id INTEGER PRIMARY KEY,
          quest_name TEXT NOT NULL UNIQUE,
          quest_type TEXT NOT NULL CHECK(quest_type IN ('主线', '支线', '日常')),
          priority_level TEXT NOT NULL CHECK(priority_level IN ('紧急', '重要', '普通')),
          target_desc TEXT NOT NULL CHECK(LENGTH(target_desc) <= 100),
          progress_text TEXT NOT NULL CHECK(progress_text = '0%' OR progress_text GLOB '[1-9]%' OR progress_text GLOB '[1-9][0-9]%' OR progress_text = '100%'),
          status_tag TEXT NOT NULL CHECK(status_tag IN ('进行中', '已完成', '已失败', '已放弃')),
          source_text TEXT,
          reward_text TEXT
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS chronicle (
          row_id INTEGER PRIMARY KEY,
          code_index TEXT NOT NULL UNIQUE CHECK(code_index GLOB 'AM[0-9][0-9][0-9][0-9]'),
          time_span TEXT NOT NULL CHECK(time_span GLOB '????-??-?? ??:?? ~ ????-??-?? ??:??'),
          summary TEXT NOT NULL CHECK(LENGTH(summary) <= 30),
          chronicle_text TEXT NOT NULL CHECK(LENGTH(chronicle_text) >= 100 AND LENGTH(chronicle_text) <= 2000)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS character_memory (
          row_id INTEGER PRIMARY KEY,
          character_name TEXT NOT NULL,
          round_index TEXT NOT NULL,
          memory_text TEXT NOT NULL CHECK(LENGTH(memory_text) <= 400),
          emotional_state TEXT,
          created_at TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS save_points (
          save_id INTEGER PRIMARY KEY,
          chapter INT NOT NULL,
          trigger_reason TEXT NOT NULL,
          global_state_snapshot TEXT NOT NULL,
          protagonist_snapshot TEXT NOT NULL,
          world_map_snapshot TEXT NOT NULL DEFAULT '[]',
          map_elements_snapshot TEXT NOT NULL DEFAULT '[]',
          factions_snapshot TEXT NOT NULL DEFAULT '[]',
          npc_snapshot TEXT NOT NULL,
          inventory_snapshot TEXT NOT NULL,
          equipment_snapshot TEXT NOT NULL,
          quest_snapshot TEXT NOT NULL,
          created_at TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS death_return_log (
          log_id INTEGER PRIMARY KEY,
          loop_count INTEGER NOT NULL,
          death_cause TEXT NOT NULL,
          miasma_level INTEGER NOT NULL DEFAULT 0,
          save_point_id INTEGER REFERENCES save_points(save_id),
          chronicle_index TEXT,
          created_at TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS agent_config (
          config_id INTEGER PRIMARY KEY,
          agent_type TEXT NOT NULL CHECK(agent_type IN ('GM', 'Character', 'Form')),
          agent_name TEXT NOT NULL UNIQUE,
          api_endpoint TEXT NOT NULL,
          api_key TEXT NOT NULL,
          model_name TEXT NOT NULL,
          temperature REAL DEFAULT 0.7 CHECK(temperature >= 0 AND temperature <= 2),
          max_tokens INTEGER DEFAULT 4096 CHECK(max_tokens > 0),
          system_prompt TEXT,
          enabled INTEGER DEFAULT 1 CHECK(enabled IN (0, 1)),
          max_input_tokens INTEGER DEFAULT 4096,
          response_format TEXT DEFAULT 'JSON',
          enable_thinking INTEGER DEFAULT 0,
          reasoning_effort TEXT DEFAULT 'medium',
          auto_retry INTEGER DEFAULT 1
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS protagonist_templates (
          template_id INTEGER PRIMARY KEY,
          template_name TEXT NOT NULL UNIQUE,
          includes_subaru INTEGER DEFAULT 1,
          base_data TEXT NOT NULL,
          is_default INTEGER DEFAULT 0
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS api_routing (
          routing_key TEXT PRIMARY KEY,
          preset_name TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS chat_sessions (
          session_id INTEGER PRIMARY KEY AUTOINCREMENT,
          session_name TEXT NOT NULL,
          is_active INTEGER DEFAULT 0 CHECK(is_active IN (0, 1)),
          created_at TEXT NOT NULL,
          global_state_snapshot TEXT NOT NULL DEFAULT '{{}}',
          protagonist_snapshot TEXT NOT NULL DEFAULT '{{}}',
          world_map_snapshot TEXT NOT NULL DEFAULT '[]',
          map_elements_snapshot TEXT NOT NULL DEFAULT '[]',
          factions_snapshot TEXT NOT NULL DEFAULT '[]',
          npc_snapshot TEXT NOT NULL DEFAULT '[]',
          inventory_snapshot TEXT NOT NULL DEFAULT '[]',
          equipment_snapshot TEXT NOT NULL DEFAULT '[]',
          quest_snapshot TEXT NOT NULL DEFAULT '[]',
          chronicle_snapshot TEXT NOT NULL DEFAULT '[]',
          character_memory_snapshot TEXT NOT NULL DEFAULT '[]',
          death_return_log_snapshot TEXT NOT NULL DEFAULT '[]',
          save_points_snapshot TEXT NOT NULL DEFAULT '[]',
          detailed_rounds_snapshot TEXT NOT NULL DEFAULT '[]'
        );
        """
    ];

    public static readonly (string Name, string Definition)[] SavePointUpgradeColumns =
    [
        ("world_map_snapshot", "TEXT NOT NULL DEFAULT '[]'"),
        ("map_elements_snapshot", "TEXT NOT NULL DEFAULT '[]'"),
        ("factions_snapshot", "TEXT NOT NULL DEFAULT '[]'")
    ];
}
