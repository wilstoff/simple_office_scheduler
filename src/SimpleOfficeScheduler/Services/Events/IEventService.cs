using NodaTime;
using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Services.Events;

public interface IEventService
{
    Task<Event> CreateEventAsync(Event evt, int ownerUserId);
    Task<Event?> GetEventAsync(int eventId);
    Task<List<Event>> SearchEventsAsync(string? searchTerm);
    Task<List<EventOccurrence>> GetOccurrencesInRangeAsync(LocalDateTime start, LocalDateTime end);
    Task<EventOccurrence?> GetOccurrenceAsync(int occurrenceId);
    Task<(bool Success, string? Error)> SignUpAsync(int occurrenceId, int userId, string message);
    Task<(bool Success, string? Error)> CancelSignUpAsync(int occurrenceId, int userId);
    Task<(bool Success, string? Error)> CancelOccurrenceAsync(int occurrenceId, int userId);
    Task<(bool Success, string? Error)> UncancelOccurrenceAsync(int occurrenceId, int userId);
    Task<(bool Success, string? Error)> UpdateEventAsync(Event evt, int userId);
    Task<(bool Success, string? Error)> TransferOwnershipAsync(int eventId, int currentOwnerId, int newOwnerId);
    Task<(bool Success, string? Error)> DeleteEventAsync(int eventId, int userId);
    Task<(bool Success, string? Error)> SetContributorsAsync(int occurrenceId, int userId, List<int> contributorUserIds);
    Task<(bool Success, string? Error)> ToggleLightningTalksAsync(int occurrenceId, int userId, bool isLightningTalks, int? capacity = null);
    Task<(bool Success, string? Error)> UpdateOccurrenceNameAsync(int occurrenceId, int userId, string? namePrefix, string? nameSuffix);
    Task<(bool Success, string? Error)> SetReminderDefinitionsAsync(int eventId, int userId, List<string> reminderNames);
    Task<(bool Success, string? Error)> SetReminderValueAsync(int occurrenceId, int userId, int reminderDefinitionId, bool value);
}
