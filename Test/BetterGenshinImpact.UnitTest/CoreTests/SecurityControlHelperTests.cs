using System.Security.AccessControl;
using System.Security.Principal;
using BetterGenshinImpact.Helpers;

namespace BetterGenshinImpact.UnitTest.CoreTests;

public class SecurityControlHelperTests
{
    [Fact]
    public void ExistingFullInheritedPermissionsDoNotRewriteDirectoryTree()
    {
        // In-memory descriptor only: never call App, elevation or real ACL persistence.
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm("D:(A;OICI;FA;;;WD)(A;OICI;FA;;;BU)");
        var writes = 0;

        SecurityControlHelper.EnsureFullFolderSecurity(security, _ => writes++);

        Assert.Equal(0, writes);
    }

    [Theory]
    [InlineData("D:")]
    [InlineData("D:(A;OICI;FA;;;WD)")]
    [InlineData("D:(A;OICI;FR;;;WD)(A;OICI;FR;;;BU)")]
    [InlineData("D:(A;;FA;;;WD)(A;;FA;;;BU)")]
    [InlineData("D:(A;OICINP;FA;;;WD)(A;OICINP;FA;;;BU)")]
    [InlineData("D:(A;OICIIO;FA;;;WD)(A;OICIIO;FA;;;BU)")]
    public void MissingOrPartialPermissionsArePersistedOnceAndThenStable(string sddl)
    {
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(sddl);
        var writes = 0;

        SecurityControlHelper.EnsureFullFolderSecurity(security, _ => writes++);

        Assert.Equal(1, writes);
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToArray();
        foreach (var sid in new[] { "S-1-1-0", "S-1-5-32-545" })
        {
            Assert.Contains(rules, rule => rule.IdentityReference.Value == sid
                && rule.AccessControlType == AccessControlType.Allow
                && rule.FileSystemRights == FileSystemRights.FullControl
                && rule.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)
                && rule.PropagationFlags == PropagationFlags.None);
        }

        SecurityControlHelper.EnsureFullFolderSecurity(security, _ => writes++);
        Assert.Equal(1, writes);
    }

    [Fact]
    public void UnrelatedRulesOwnerGroupAndProtectionArePreserved()
    {
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm("O:BAG:SYD:P(D;OICI;WD;;;WD)(A;OICI;FA;;;SY)");
        var ownerAndGroup = security.GetSecurityDescriptorSddlForm(
            AccessControlSections.Owner | AccessControlSections.Group);
        var writes = 0;

        SecurityControlHelper.EnsureFullFolderSecurity(security, _ => writes++);

        Assert.Equal(1, writes);
        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(ownerAndGroup, security.GetSecurityDescriptorSddlForm(
            AccessControlSections.Owner | AccessControlSections.Group));
        var access = security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        Assert.Contains("(D;OICI;WD;;;WD)", access);
        Assert.Contains("(A;OICI;FA;;;SY)", access);
    }

    [Fact]
    public void FailedPersistenceIsNotReportedAsSuccess()
    {
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm("D:");
        var failure = new UnauthorizedAccessException("isolated write failure");

        var actual = Assert.Throws<UnauthorizedAccessException>(() =>
            SecurityControlHelper.EnsureFullFolderSecurity(security, _ => throw failure));

        Assert.Same(failure, actual);
    }
}
