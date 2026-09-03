// 手書き実装。VS の ResX デザイナ生成は SDK スタイルの build で確実に走らないため、
// resx (Kudaki.App.Properties.Strings.resources) を直接 ResourceManager で引く形にしている。
// resx にキーを足したらここにも静的プロパティを1行足す (キーは英中立側 resx に必ず入れる)。
#nullable enable
using System.Globalization;
using System.Resources;

namespace Kudaki.App.Properties;

public static class Strings
{
    // resx の manifest resource name は SDK 既定で「<RootNamespace>.<Path>.<basename>」。
    // Kudaki.App の RootNamespace は Kudaki.App、置き場は Properties/Strings.resx なので
    // これで一意に解決される。
    public static ResourceManager ResourceManager { get; } =
        new("Kudaki.App.Properties.Strings", typeof(Strings).Assembly);

    private static string Get(string key)
        => ResourceManager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    // Common
    public static string Common_Ok => Get("Common_Ok");
    public static string Common_Cancel => Get("Common_Cancel");
    public static string Common_Untitled => Get("Common_Untitled");
    public static string Common_Value_Unset => Get("Common_Value_Unset");

    // Main window / title
    public static string Main_Title_Untitled => Get("Main_Title_Untitled");
    public static string Main_Title_Format => Get("Main_Title_Format");

    // File menu
    public static string Menu_File => Get("Menu_File");
    public static string Menu_File_New => Get("Menu_File_New");
    public static string Menu_File_Open => Get("Menu_File_Open");
    public static string Menu_File_Save => Get("Menu_File_Save");
    public static string Menu_File_SaveAs => Get("Menu_File_SaveAs");
    public static string Menu_File_ExportMarkdown => Get("Menu_File_ExportMarkdown");
    public static string Menu_File_Exit => Get("Menu_File_Exit");

    // Edit menu
    public static string Menu_Edit => Get("Menu_Edit");
    public static string Menu_Edit_AddSibling => Get("Menu_Edit_AddSibling");
    public static string Menu_Edit_AddChild => Get("Menu_Edit_AddChild");
    public static string Menu_Edit_Indent => Get("Menu_Edit_Indent");
    public static string Menu_Edit_Outdent => Get("Menu_Edit_Outdent");
    public static string Menu_Edit_MoveUp => Get("Menu_Edit_MoveUp");
    public static string Menu_Edit_MoveDown => Get("Menu_Edit_MoveDown");
    public static string Menu_Edit_Delete => Get("Menu_Edit_Delete");

    // Tools menu
    public static string Menu_Tools => Get("Menu_Tools");
    public static string Menu_Tools_Preferences => Get("Menu_Tools_Preferences");

    // Help menu
    public static string Menu_Help => Get("Menu_Help");
    public static string Menu_Help_OpenRepository => Get("Menu_Help_OpenRepository");

    // Titlebar
    public static string Caption_Minimize => Get("Caption_Minimize");
    public static string Caption_Maximize => Get("Caption_Maximize");
    public static string Caption_Restore => Get("Caption_Restore");
    public static string Caption_Close => Get("Caption_Close");
    public static string Titlebar_UpdateAvailable_Prefix => Get("Titlebar_UpdateAvailable_Prefix");

    // Status bar
    public static string StatusBar_ShortcutHint => Get("StatusBar_ShortcutHint");

    // Tree
    public static string Tree_ContextMenu_ShowArrowDiagram => Get("Tree_ContextMenu_ShowArrowDiagram");
    public static string Tree_Tooltip_HierarchyLevel => Get("Tree_Tooltip_HierarchyLevel");
    public static string Tree_Tooltip_NeedsBreakdown => Get("Tree_Tooltip_NeedsBreakdown");
    public static string Tree_Empty_Hint_Line1 => Get("Tree_Empty_Hint_Line1");
    public static string Tree_Empty_Hint_Line2 => Get("Tree_Empty_Hint_Line2");

    // Detail panel
    public static string Detail_Header => Get("Detail_Header");
    public static string Detail_Title => Get("Detail_Title");
    public static string Detail_Estimate => Get("Detail_Estimate");
    public static string Detail_Remaining => Get("Detail_Remaining");
    public static string Detail_Remaining_Suffix => Get("Detail_Remaining_Suffix");
    public static string Detail_Assignee => Get("Detail_Assignee");
    public static string Detail_DueDate => Get("Detail_DueDate");
    public static string Detail_Notes => Get("Detail_Notes");
    public static string Detail_Predecessors => Get("Detail_Predecessors");
    public static string Detail_Predecessors_Suffix => Get("Detail_Predecessors_Suffix");
    public static string Detail_Predecessor_RemoveTooltip => Get("Detail_Predecessor_RemoveTooltip");
    public static string Detail_Predecessor_AddTooltip => Get("Detail_Predecessor_AddTooltip");
    public static string Detail_Summary_Header => Get("Detail_Summary_Header");
    public static string Detail_Summary_EstimateTotal => Get("Detail_Summary_EstimateTotal");
    public static string Detail_Summary_RemainingTotal => Get("Detail_Summary_RemainingTotal");
    public static string Detail_Summary_ActualTotal => Get("Detail_Summary_ActualTotal");
    public static string Detail_Summary_Progress => Get("Detail_Summary_Progress");
    public static string Detail_Warning_Line1 => Get("Detail_Warning_Line1");
    public static string Detail_Warning_Line2 => Get("Detail_Warning_Line2");

    // Diff overlay
    public static string Diff_Header => Get("Diff_Header");
    public static string Diff_SourceLabel => Get("Diff_SourceLabel");
    public static string Diff_Source_Unknown => Get("Diff_Source_Unknown");
    public static string Diff_Changes_CountFormat => Get("Diff_Changes_CountFormat");
    public static string Diff_Reject => Get("Diff_Reject");
    public static string Diff_Approve => Get("Diff_Approve");
    public static string Diff_DocumentLevelPseudoTitle => Get("Diff_DocumentLevelPseudoTitle");

    // Landing
    public static string Landing_Product => Get("Landing_Product");
    public static string Landing_Tagline => Get("Landing_Tagline");
    public static string Landing_Status_Startup => Get("Landing_Status_Startup");
    public static string Landing_Status_Initializing => Get("Landing_Status_Initializing");
    public static string Landing_Status_McpStarted => Get("Landing_Status_McpStarted");
    public static string Landing_Status_McpFailed => Get("Landing_Status_McpFailed");
    public static string Landing_Status_Ready => Get("Landing_Status_Ready");

    // Load / save status
    public static string Status_Loading_Format => Get("Status_Loading_Format");
    public static string Status_Building => Get("Status_Building");
    public static string Status_Complete => Get("Status_Complete");
    public static string Status_Loaded_Format => Get("Status_Loaded_Format");
    public static string Status_LoadFailed_Format => Get("Status_LoadFailed_Format");
    public static string Status_Saved_Format => Get("Status_Saved_Format");
    public static string Status_SaveFailed_Format => Get("Status_SaveFailed_Format");
    public static string Status_MarkdownExported_Format => Get("Status_MarkdownExported_Format");
    public static string Status_MarkdownExportFailed_Format => Get("Status_MarkdownExportFailed_Format");
    public static string Status_AiProposalApplied => Get("Status_AiProposalApplied");
    public static string Status_PredecessorAdded_Format => Get("Status_PredecessorAdded_Format");
    public static string Status_PredecessorRemoved_Format => Get("Status_PredecessorRemoved_Format");

    // File dialogs
    public static string Dialog_Open_Title => Get("Dialog_Open_Title");
    public static string Dialog_SaveAs_Title => Get("Dialog_SaveAs_Title");
    public static string Dialog_MarkdownExport_Title => Get("Dialog_MarkdownExport_Title");
    public static string Dialog_Markdown_Filter => Get("Dialog_Markdown_Filter");

    // Tab close confirm (t-tab-close)
    public static string CloseTab_Confirm_Title => Get("CloseTab_Confirm_Title");
    public static string CloseTab_Confirm_Message_Format => Get("CloseTab_Confirm_Message_Format");
    public static string CloseTab_Confirm_Message_Untitled => Get("CloseTab_Confirm_Message_Untitled");
    public static string CloseTab_Button_Save => Get("CloseTab_Button_Save");
    public static string CloseTab_Button_Discard => Get("CloseTab_Button_Discard");
    public static string CloseTab_Button_Cancel => Get("CloseTab_Button_Cancel");
    public static string TabHeader_CloseTooltip => Get("TabHeader_CloseTooltip");

    // Preferences: MCP category (v03-mcp-auto-apply t-settings-model)
    public static string Preferences_Category_Mcp => Get("Preferences_Category_Mcp");
    public static string Preferences_Mcp_AutoApply_Label => Get("Preferences_Mcp_AutoApply_Label");
    public static string Preferences_Mcp_AutoApply_Hint => Get("Preferences_Mcp_AutoApply_Hint");

    // Arrow diagram
    public static string Arrow_Title_Format => Get("Arrow_Title_Format");
    public static string Arrow_Legend_Line1 => Get("Arrow_Legend_Line1");
    public static string Arrow_Legend_Line2 => Get("Arrow_Legend_Line2");
    public static string Arrow_Node_Tooltip_ExternalInbound => Get("Arrow_Node_Tooltip_ExternalInbound");
    public static string Arrow_Node_Estimate_Prefix => Get("Arrow_Node_Estimate_Prefix");
    public static string Arrow_Node_Remaining_Prefix => Get("Arrow_Node_Remaining_Prefix");
    public static string Arrow_Node_Hours_Suffix => Get("Arrow_Node_Hours_Suffix");

    // Update prompt
    public static string Update_OpenInBrowser => Get("Update_OpenInBrowser");
    public static string Update_InstallNow => Get("Update_InstallNow");
    public static string Update_Title_Format => Get("Update_Title_Format");
    public static string Update_Message_AutoUpdate_Format => Get("Update_Message_AutoUpdate_Format");
    public static string Update_Message_Manual_Format => Get("Update_Message_Manual_Format");
    public static string Update_Progress_Format => Get("Update_Progress_Format");
    public static string Update_Error_DownloadFailed_Format => Get("Update_Error_DownloadFailed_Format");

    // Dependency validation
    public static string Dep_Error_Self => Get("Dep_Error_Self");
    public static string Dep_Error_AlreadyRegistered => Get("Dep_Error_AlreadyRegistered");
    public static string Dep_Error_AncestryRelation => Get("Dep_Error_AncestryRelation");
    public static string Dep_Error_Cycle => Get("Dep_Error_Cycle");

    // YAML storage
    public static string Storage_Error_VersionTooNew_Format => Get("Storage_Error_VersionTooNew_Format");
    public static string Storage_Error_LoadFailed_Format => Get("Storage_Error_LoadFailed_Format");
    public static string Storage_Error_EmptyYaml => Get("Storage_Error_EmptyYaml");
    public static string Storage_Error_YamlParse_Format => Get("Storage_Error_YamlParse_Format");
    public static string Storage_Error_UnableToRestore => Get("Storage_Error_UnableToRestore");
    public static string Storage_YamlSaveFilter => Get("Storage_YamlSaveFilter");
    public static string Storage_YamlOpenFilter => Get("Storage_YamlOpenFilter");

    // Markdown export
    public static string Md_FallbackDocumentTitle => Get("Md_FallbackDocumentTitle");
    public static string Md_UpdatedLine_Format => Get("Md_UpdatedLine_Format");
    public static string Md_NoTasks => Get("Md_NoTasks");
    public static string Md_UntitledTask => Get("Md_UntitledTask");
    public static string Md_Warning_Suffix => Get("Md_Warning_Suffix");
    public static string Md_Leaf_Estimate_Format => Get("Md_Leaf_Estimate_Format");
    public static string Md_Leaf_Remaining_Format => Get("Md_Leaf_Remaining_Format");
    public static string Md_Leaf_Progress_Format => Get("Md_Leaf_Progress_Format");
    public static string Md_Inner_EstimateTotal_Format => Get("Md_Inner_EstimateTotal_Format");
    public static string Md_Inner_RemainingTotal_Format => Get("Md_Inner_RemainingTotal_Format");
    public static string Md_Inner_Progress_Format => Get("Md_Inner_Progress_Format");
    public static string Md_Meta_Assignee_Format => Get("Md_Meta_Assignee_Format");
    public static string Md_Meta_DueDate_Format => Get("Md_Meta_DueDate_Format");

    // Preferences dialog
    public static string Preferences_Title => Get("Preferences_Title");
    public static string Preferences_Category_General => Get("Preferences_Category_General");
    public static string Preferences_Language_Label => Get("Preferences_Language_Label");
    public static string Preferences_Language_Hint => Get("Preferences_Language_Hint");
    public static string Preferences_Language_System => Get("Preferences_Language_System");
    public static string TabHeader_PendingApprovalTooltip => Get("TabHeader_PendingApprovalTooltip");
    public static string Preferences_Notify_Header => Get("Preferences_Notify_Header");
    public static string Preferences_Notify_Hint => Get("Preferences_Notify_Hint");
    public static string Preferences_Notify_Sound_Label => Get("Preferences_Notify_Sound_Label");
    public static string Preferences_Notify_Flash_Label => Get("Preferences_Notify_Flash_Label");
    public static string Preferences_Notify_Restore_Label => Get("Preferences_Notify_Restore_Label");
    public static string Preferences_Notify_Repeat_Label => Get("Preferences_Notify_Repeat_Label");
}
