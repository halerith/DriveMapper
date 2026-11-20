#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DriveMapper;

internal enum OutcomeKind { Success, TransientFailure, ConfigurationError, AccessError, Deferred }
internal sealed record DriveOutcome(string Letter, OutcomeKind Kind, string Message = "", int? NativeError = null);
internal sealed record AgentRequest(string[]? Letters = null);
internal sealed record AgentReport(int SessionId, string UserSid, string LogonId, List<DriveOutcome> Drives);

internal static class AgentReportRules
{
    public static void Validate(AgentReport report, UserContext expected)
    {
        if (report.SessionId != expected.SessionId || report.UserSid != expected.Sid || report.LogonId != expected.LogonId ||
            report.Drives == null || report.Drives.Count > 27 || report.Drives.Any(d => d == null) ||
            report.Drives.Select(d => d.Letter).Distinct().Count() != report.Drives.Count)
            throw new InvalidDataException("Agent report does not match the target session.");
        foreach (var drive in report.Drives)
            if (drive.Letter == null || drive.Letter != "*" && (drive.Letter.Length != 1 || drive.Letter[0] is < 'A' or > 'Z') ||
                !Enum.IsDefined(drive.Kind) || drive.Message == null || drive.Message.Length > 8192)
                throw new InvalidDataException("Invalid per-drive Agent outcome.");
    }
}
