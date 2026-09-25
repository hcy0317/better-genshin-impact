using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.View.Windows;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Helpers;

public static class SecurityControlHelper
{

    public static void AllowFullFolderSecurity(string dirPath)
    {
        if (!RuntimeHelper.IsElevated)
        {
            return;
        }

        try
        {
            DirectoryInfo dir = new(dirPath);
            DirectorySecurity dirSecurity = dir.GetAccessControl(AccessControlSections.All);
            EnsureFullFolderSecurity(dirSecurity, dir.SetAccessControl);
        }
        catch (Exception e)
        {
            TaskControl.Logger.LogError("首次运行自动初始化按键绑定异常：" + e.Source + "\r\n--" + Environment.NewLine + e.StackTrace + "\r\n---" + Environment.NewLine + e.Message);
            ThemedMessageBox.Warning("检测到当前 BetterGI 位于C盘，尝试修改目录权限失败，可能会导致WebView2相关的功能无法使用！" + e.Message);
        }
    }

    internal static void EnsureFullFolderSecurity(DirectorySecurity security, Action<DirectorySecurity> persist)
    {
        var originalAccess = security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        InheritanceFlags inherits = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        FileSystemAccessRule everyoneRule = new(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.FullControl, inherits, PropagationFlags.None, AccessControlType.Allow);
        FileSystemAccessRule usersRule = new(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.FullControl, inherits, PropagationFlags.None, AccessControlType.Allow);
        security.ModifyAccessRule(AccessControlModification.Add, everyoneRule, out _);
        security.ModifyAccessRule(AccessControlModification.Add, usersRule, out _);
        // Even an identical DACL write propagates to the entire installation tree.
        // ModifyAccessRule's modified flag does not indicate a semantic change.
        if (!string.Equals(originalAccess, security.GetSecurityDescriptorSddlForm(AccessControlSections.Access),
                StringComparison.Ordinal))
        {
            persist(security);
        }
    }
}
