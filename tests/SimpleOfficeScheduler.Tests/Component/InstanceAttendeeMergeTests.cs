using Microsoft.Graph.Models;
using SimpleOfficeScheduler.Models;
using SimpleOfficeScheduler.Services.Calendar;

namespace SimpleOfficeScheduler.Tests;

/// <summary>
/// A workshop is one Graph recurring series. Signing up for a single occurrence patches that
/// instance's attendee list, which Graph records as an exception. The room is booked on the series
/// as a resource attendee, so the patch has to carry it forward — replacing the list outright
/// cancels the room's hold on that one date while the rest of the series keeps it.
/// </summary>
public class InstanceAttendeeMergeTests
{
    private static AppUser User(string name, string email) => new()
    {
        DisplayName = name,
        Email = email,
        Username = name.ToLowerInvariant()
    };

    private static EventSignup Signup(AppUser user, string message = "") => new()
    {
        User = user,
        UserId = user.Id,
        Message = message
    };

    private static readonly Attendee RoomAttendee = new()
    {
        EmailAddress = new EmailAddress { Address = "training-room@corp.com", Name = "Training Room" },
        Type = AttendeeType.Resource
    };

    [Fact]
    public void RoomResourceOnTheInstance_IsPreserved()
    {
        var owners = new[] { User("Owner", "owner@corp.com") };
        var signups = new[] { Signup(User("Attendee", "attendee@corp.com")) };

        var result = GraphCalendarService.MergeInstanceAttendees(new[] { RoomAttendee }, owners, signups);

        var room = Assert.Single(result, a => a.Type == AttendeeType.Resource);
        Assert.Equal("training-room@corp.com", room.EmailAddress!.Address);
    }

    [Fact]
    public void OwnersAreRequired_AndSignupsAreOptional()
    {
        var owners = new[] { User("Owner", "owner@corp.com"), User("CoOwner", "co@corp.com") };
        var signups = new[] { Signup(User("Attendee", "attendee@corp.com")) };

        var result = GraphCalendarService.MergeInstanceAttendees(new[] { RoomAttendee }, owners, signups);

        Assert.Equal(
            new[] { "owner@corp.com", "co@corp.com" },
            result.Where(a => a.Type == AttendeeType.Required).Select(a => a.EmailAddress!.Address));
        Assert.Equal(
            new[] { "attendee@corp.com" },
            result.Where(a => a.Type == AttendeeType.Optional).Select(a => a.EmailAddress!.Address));
    }

    [Fact]
    public void AnOwnerWhoAlsoSignedUp_AppearsOnceAsRequired()
    {
        var owner = User("Owner", "owner@corp.com");
        var result = GraphCalendarService.MergeInstanceAttendees(
            new[] { RoomAttendee }, new[] { owner }, new[] { Signup(owner) });

        var matches = result.Where(a =>
            a.EmailAddress!.Address == "owner@corp.com").ToList();
        Assert.Single(matches);
        Assert.Equal(AttendeeType.Required, matches[0].Type);
    }

    [Fact]
    public void NoRoomOnTheInstance_AddsNone()
    {
        var result = GraphCalendarService.MergeInstanceAttendees(
            Array.Empty<Attendee>(), new[] { User("Owner", "owner@corp.com") }, Array.Empty<EventSignup>());

        Assert.DoesNotContain(result, a => a.Type == AttendeeType.Resource);
        Assert.Single(result);
    }

    [Fact]
    public void NullExistingAttendees_IsHandled()
    {
        var result = GraphCalendarService.MergeInstanceAttendees(
            null, new[] { User("Owner", "owner@corp.com") }, Array.Empty<EventSignup>());

        Assert.Single(result);
    }

    [Fact]
    public void StalePeopleAttendeesFromAPreviousPatch_AreNotCarriedForward()
    {
        // Someone who cancelled must actually come off the instance, so only the resource survives.
        var existing = new[]
        {
            RoomAttendee,
            new Attendee
            {
                EmailAddress = new EmailAddress { Address = "gone@corp.com", Name = "Gone" },
                Type = AttendeeType.Optional
            }
        };

        var result = GraphCalendarService.MergeInstanceAttendees(
            existing, new[] { User("Owner", "owner@corp.com") }, Array.Empty<EventSignup>());

        Assert.DoesNotContain("gone@corp.com", result.Select(a => a.EmailAddress!.Address));
        Assert.Contains("training-room@corp.com", result.Select(a => a.EmailAddress!.Address));
    }

    [Fact]
    public void MultipleResources_AreAllPreserved()
    {
        var second = new Attendee
        {
            EmailAddress = new EmailAddress { Address = "projector@corp.com", Name = "Projector" },
            Type = AttendeeType.Resource
        };

        var result = GraphCalendarService.MergeInstanceAttendees(
            new[] { RoomAttendee, second }, new[] { User("Owner", "owner@corp.com") }, Array.Empty<EventSignup>());

        Assert.Equal(2, result.Count(a => a.Type == AttendeeType.Resource));
    }
}
