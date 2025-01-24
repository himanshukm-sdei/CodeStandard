using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;


using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;


using Orleans;
using Orleans.Runtime;

using Serilog;

using WA.Data.RestService.Interface;
using WA.Domain.Common.Command;
using WA.Domain.Common.Enumeration;
using WA.Domain.Common.Event;
using WA.Domain.Coordination;
using WA.Domain.TypedIdentity;
using WA.Domain.User;
using WA.Domain.User.Command;
using WA.Domain.User.Event;
using WA.EventPersistence.Interfaces.Store;
using WA.GrainInterface.Read;
using WA.GrainInterface.Write;
using WA.Sagas;

using WA.Shared;
using WA.Shared.Configuration;
using WA.Shared.DTO;
using WA.Shared.ServiceInterface;

using FeaturedStoriesTopicPromptDismissed = WA.Domain.User.Event.FeaturedStoriesTopicPromptDismissed;
using FreeTrialPromptDismissed = WA.Domain.User.Event.FreeTrialPromptDismissed;
using GroupPromptDismissed = WA.Domain.User.Event.GroupPromptDismissed;
using MiloPreferencesAdded = WA.Domain.User.Event.MiloPreferencesAdded;
using MiloPreferencesUpdated = WA.Domain.User.Event.MiloPreferencesUpdated;
using SearchPromptDismissed = WA.Domain.User.Event.SearchPromptDismissed;
using StoryPromptDismissed = WA.Domain.User.Event.StoryPromptDismissed;
using User = WA.Domain.Common.User;

namespace WA.CommandHandler.Grain;

public class UserResource(
    ISagaCoordinator sagaCoordinator,
    IEventStore eventStore,
    IDateTimeProvider dateTimeProvider,
    ILogger<UserResource> logger,
    IOptionsMonitor<ApplicationOptions> appOptions,
    ISubscriptionDataService subscriptionDataService,
    IAuthProviderService authProviderService,
    IRecurlyService recurlyService)
    : EventSourcedGrain<User, UserState>(sagaCoordinator, logger, eventStore, appOptions), IUserWrite, IUserRead
{
    private readonly IDateTimeProvider _dateTimeProvider = dateTimeProvider;
    private readonly ISubscriptionDataService _subscriptionDataService = subscriptionDataService;
    private UserId _userId;
    private DateTime? _createdAt;
    private readonly IAuthProviderService _authProviderService = authProviderService;
    private readonly IRecurlyService _recurlyService = recurlyService;
    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await base.OnActivateAsync(cancellationToken);
        _userId = new(this.GetPrimaryKey());
    }
    internal Task<CommandOutcome> Handle(AssignRepresentingPersonCommand assignRepresentingPersonCommand)
    {
        var outcome = CommandOutcome.OK;

        var representingPersonAssigned = new RepresentingPersonAssigned(assignRepresentingPersonCommand.UserId,
                                                                        assignRepresentingPersonCommand.RepresentingPersonId);

        outcome = outcome with { DomainEvent = representingPersonAssigned };

        var representingPersonProfileImageUpdateInNewTreeRequestedFrom =
            RepresentingPersonProfileImageUpdateInNewTreeRequestedFrom(assignRepresentingPersonCommand.UserId,
                                                                       assignRepresentingPersonCommand.RepresentingPersonId);
        outcome = outcome with
        {
            CoordinationEvent = representingPersonProfileImageUpdateInNewTreeRequestedFrom
        };

        return Task.FromResult(outcome);
    }

    internal Task<CommandOutcome> Handle(AssignRepresentingGroupMemberCommand assignRepresentingGroupMemberCommand)
    {
        var outcome = CommandOutcome.OK;
        var groupMemberRepresentingPersonAssigned = new GroupMemberRepresentingPersonAssigned(assignRepresentingGroupMemberCommand.UserId, assignRepresentingGroupMemberCommand.PersonId, assignRepresentingGroupMemberCommand.PersonOwnerId);
        outcome = outcome with { DomainEvent = groupMemberRepresentingPersonAssigned };
        return Task.FromResult(outcome);
    }

    internal Task<CommandOutcome> Handle(InitializeUserPreferencesCommand _)
    {
        var outcome = CommandOutcome.OK;
        if ((State.Preferences.WidgetSettings.ShouldDisplaySearchPrompt || State.Preferences.WidgetSettings.ShouldDisplayGroupPrompt ||
            State.Preferences.WidgetSettings.ShouldDisplayStoryPrompt) && State.Preferences?.WidgetSettings?.WidgetDisplayOrder.Count <= 0)
        {
            outcome = outcome with { DomainEvent = new UserPreferencesInitialized(_userId, BuildPromptDisplayOrder()) };
        }

        return Task.FromResult(outcome);
    }

    internal Task<CommandOutcome> Handle(MigrateNAUserToCommand migrateNAUser)
    {
        if (!State.IsEmpty)
        {
            return Task.FromResult(CommandOutcome.Error("User Exists", $"Cannot create user with UserId {migrateNAUserTo.UserId}; it already exists."));
        }

        var naUserMigrated =
            new NAUserMigrated(migrateNAUser.UserId);
        return Task.FromResult(CommandOutcome.OK with { DomainEvent = naUserMigrated });
    }

    internal Task<CommandOutcome> Handle(UpdateUserProfileCommand _)
    {
        var updateUserProfile = new UserProfileUpdated(_userId);
        return Task.FromResult(CommandOutcome.OK with { DomainEvent = updateUserProfile });
    }

    internal async Task<CommandOutcome> Handle(EnsureUserExistsCommand ensureUserExistsCommand)
    {
        if (!await IsNew())
            return CommandOutcome.OK;

        var outcome = await Handle(new CreateUserCommand(AccountSource.ApplicationName, string.Empty));

        var events = outcome.DomainEvents.Where(e => e.EventType != "AppNameUserCreated");

        var auth0UserId = RequestContext.Get("Auth0UserId") != null ? RequestContext.Get("Auth0UserId").ToString() : string.Empty;
        var bareUserCreated = new BareUserCreated(_userId, auth0UserId, ensureAppNameUserExistsCommand.AttemptedTask);
        var domainEvents = new List<DomainEvent>
        {
            bareUserCreated
        };
        domainEvents.AddRange(events);

        return CommandOutcome.OK with { DomainEvents = domainEvents, CoordinationEvents = outcome.CoordinationEvents };
    }

    internal async Task<CommandOutcome> Handle(CreateAppNameUserCommand createAppNameUserCommand)
    {
        try
        {
            Log.Information("Execution Started of CreateAppNameUserCommand for UserId-{0}", this.GetPrimaryKey());
            if (!await IsNew())
                return CommandOutcome.Error("User Exists", $"Cannot create user with UserId {State.Id}; it already exists.");

            var outcome = CommandOutcome.OK;
            var auth0UserId = RequestContext.Get("Auth0UserId") != null ? RequestContext.Get("Auth0UserId").ToString() : string.Empty;
            var AppNameUserCreated = new AppNameUserCreated(_userId, auth0UserId)
            {
                AccountSource = createAppNameUserCommand.AccountSource
            };
            outcome = outcome with { DomainEvent = UserCreated };
            outcome = outcome with { DomainEvent = new UserPreferencesInitialized(_userId, BuildPromptDisplayOrder()) };

            if (!string.IsNullOrWhiteSpace(createUserCommand.FamilySearchId))
            {
                outcome = outcome with { CoordinationEvent = new RegisterFamilySearchUserRequested(_userId, createUserCommand.FamilySearchId) };
            }

            Log.Information("Execution Completed of CreateAppNameUserCommand for UserId-{0}", this.GetPrimaryKey());
            return outcome;
        }
        catch (Exception ex)
        {
            Log.Information("Getting Exception while execution of CreateAppNameUserCommand UserId-{0}, Exception Message-{1}:", this.GetPrimaryKey(), ex);
            return CommandOutcome.Error("Exception", $"Got Exception while execution of CreateAppNameUserCommand UserId-{this.GetPrimaryKey()}; Exception: {ex}");
        }
    }

    internal Task<CommandOutcome> Handle(UpdateAuth0IdCommand updateAuth0IdCommand)
    {
        var auth0IdUpdated = new Auth0IdUpdated(new UserId(updateAuth0IdCommand.UserId), updateAuth0IdCommand.Auth0UserId);
        return Task.FromResult(CommandOutcome.OK with { DomainEvent = auth0IdUpdated });
    }

    public Task<CommandOutcome> Handle(DeleteUserAccountCommand _)
    {
        if (State.AccountDeletionRequestStatus)
            return Task.FromResult(CommandOutcome.Error("User Already requested", "Already requested for account deletion."));

        var auth0UserId = RequestContext.Get("Auth0UserId") != null ? RequestContext.Get("Auth0UserId").ToString() : string.Empty;
        var userAccountDeleted = new UserAccountDeleted(_userId, _dateTimeProvider.UtcNow);
        var userAccountDeletionRequested = new UserAccountActionRequest(_userId, true, auth0UserId);
        return Task.FromResult(CommandOutcome.OK with { DomainEvent = userAccountDeleted, CoordinationEvent = userAccountDeletionRequested });
    }

    public Task<CommandOutcome> Handle(UndoDeleteUserAccountCommand _)
    {
        if (!State.AccountDeletionRequestStatus)
            return Task.FromResult(CommandOutcome.Error("User Already requested", "Already requested for undo account deletion."));

        var auth0UserId = RequestContext.Get("Auth0UserId") != null ? RequestContext.Get("Auth0UserId").ToString() : string.Empty;
        var undoUserAccountDeleted = new UndoUserAccountDeleted(_userId, _dateTimeProvider.UtcNow);
        var undoUserAccountDeletionRequested = new UserAccountActionRequest(_userId, false, auth0UserId);
        return Task.FromResult(CommandOutcome.OK with { DomainEvent = undoUserAccountDeleted, CoordinationEvent = undoUserAccountDeletionRequested });
    }

    internal async Task<CommandOutcome> Handle(MigrateUserToAuth0Command _)
    {
        try
        {
            if (!await IsNew() && !string.IsNullOrWhiteSpace(State.Auth0UserId))
            {
                return CommandOutcome.OK;
            }

            var outcome = CommandOutcome.OK;
            var auth0UserId = RequestContext.Get("Auth0UserId") != null ? RequestContext.Get("Auth0UserId").ToString() : string.Empty;
            var userMigratedToAuth0 = new UserMigratedToAuth0(_userId, auth0UserId);
            outcome = outcome with { DomainEvent = userMigratedToAuth0 };
            return outcome;
        }
        catch (Exception ex)
        {
            Log.Information("Getting Exception while execution of MigrateUserCommand UserId-{0}, Exception Message-{1}:", this.GetPrimaryKey(), ex);
            return CommandOutcome.Error("Exception", $"Got Exception while execution of MigrateUserCommand UserId-{this.GetPrimaryKey()}; Exception: {ex}");
        }
    }

    public Task<CommandOutcome> Handle(AddMiloPreferencesCommand addMiloPreferencesCommand)
    {
        if (State.IsEmpty)
            return Task.FromResult(CommandOutcome.Error("User does not exist", "Cannot update user; it does not exist."));

        var miloPreferencesAdded = new MiloPreferencesAdded(State.Id, addMiloPreferencesCommand.OptInStatus, addMiloPreferencesCommand.Frequency);
        var addRemoteMiloPreferencesRequested = new AddRemoteMiloPreferencesRequested(State.Id, addMiloPreferencesCommand.DisplayName, addMiloPreferencesCommand.MobileNumber.Value, addMiloPreferencesCommand.OptInStatus, addMiloPreferencesCommand.Frequency);
        return Task.FromResult(CommandOutcome.OK with { DomainEvent = miloPreferencesAdded, CoordinationEvent = addRemoteMiloPreferencesRequested });
    }

    public Task<CommandOutcome> Handle(UpdateMiloPreferencesCommand updateMiloPreferencesCommand)
    {
        if (State.IsEmpty)
            return Task.FromResult(CommandOutcome.Error("User does not exist", "Cannot update user; it does not exist."));

        var miloPreferencesUpdated = new MiloPreferencesUpdated(State.Id, updateMiloPreferencesCommand.OptInStatus, updateMiloPreferencesCommand.Frequency);
        var updateMiloPreferencesRequested = new UpdateRemoteMiloPreferencesRequested(State.Id, updateMiloPreferencesCommand.MobileNumber.Value, updateMiloPreferencesCommand.OptInStatus, updateMiloPreferencesCommand.Frequency, updateMiloPreferencesCommand.IsNumberUpdated);
        return Task.FromResult(CommandOutcome.OK with { DomainEvent = miloPreferencesUpdated, CoordinationEvent = updateMiloPreferencesRequested });
    }

    public Task<CommandOutcome> Handle(SetUserProfileImageUrlCommand setUserProfileImageUrlCommand)
    {
        var outcome = CommandOutcome.OK;
        var userProfileImageSet = new UserProfileImageSet(setUserProfileImageUrlCommand.OwnerId, setUserProfileImageUrlCommand.MediaId,
                                                          setUserProfileImageUrlCommand.ProfileImageUrl);


        outcome = outcome with { DomainEvent = userProfileImageSet };


        var representingPersons = State.RepresentingPersons;

        if (representingPersons.Count > 0)
        {
            var representingPersonProfileImageUpdateRequestedFrom =
                RepresentingPersonProfileImageUpdateRequestedFrom(setUserProfileImageUrlCommand.MediaId,
                                                                  setUserProfileImageUrlCommand.ProfileImageUrl, representingPersons);
            outcome = outcome with { CoordinationEvent = representingPersonProfileImageUpdateRequestedFrom };
        }

        return Task.FromResult(outcome);
    }

    public Task<CommandOutcome> Handle(DeleteUserProfileImageCommand _)
    {
        var userProfileImageDeleted = new UserProfileImageDeleted(_userId);

        return Task.FromResult(CommandOutcome.OK with { DomainEvent = userProfileImageDeleted });
    }

    public Task<CommandOutcome> Handle(DeleteUserMediaCommand deleteUserMediaCommand)
    {
        var domainEvents = new List<DomainEvent>();
        if (State.ProfileImage?.Id == deleteUserMediaCommand.MediaReference.Id)
        {
            var profileImageDeleted = new UserProfileImageDeleted(_userId);
            domainEvents.Add(profileImageDeleted);
        }

        var mediaRemovedFromUser = new MediaRemovedFromUser(_userId, deleteUserMediaCommand.MediaReference.Id, deleteUserMediaCommand.MediaReference.OwnerId);
        domainEvents.Add(mediaRemovedFromUser);

        return Task.FromResult(CommandOutcome.OK with { DomainEvents = domainEvents });
    }

    internal Task<CommandOutcome> Handle(FollowUserCommand followUserCommand)
    {
        var userFollowed = new UserFollowed(State.Id, new UserId(followUserCommand.FollowToUserId));
        var followUserRequested = new FollowUnfollowUserRequested(State.Id, followUserCommand.FollowToUserId);
        return Task.FromResult(CommandOutcome.OK with { DomainEvent = userFollowed, CoordinationEvent = followUserRequested });
    }

    internal Task<CommandOutcome> Handle(UnfollowUserCommand unfollowUserCommand)
    {
        var userUnfollowed = new UserUnfollowed(State.Id, new UserId(unfollowUserCommand.UnfollowToUserId));
        var followUserRequested = new FollowUnfollowUserRequested(State.Id, unfollowUserCommand.UnfollowToUserId);
        return Task.FromResult(CommandOutcome.OK with { DomainEvent = userUnfollowed, CoordinationEvent = followUserRequested });
    }

    public Task<CommandOutcome> Handle(FollowTopicCommand followTopicCommand)
    {
        var outcome = CommandOutcome.OK;
        var topicFollowed = new TopicFollowed(_userId, new TopicId(followTopicCommand.TopicId));
        outcome = outcome with { DomainEvent = topicFollowed };
        return Task.FromResult(outcome);
    }

    public Task<CommandOutcome> Handle(UnfollowTopicCommand unfollowTopicCommand)
    {
        var topicFollowed = new TopicUnfollowed(_userId, new TopicId(unfollowTopicCommand.TopicId));
        return Task.FromResult(CommandOutcome.OK with { DomainEvent = topicFollowed });
    }

    public Task<CommandOutcome> Handle(DismissFeaturedStoriesTopicPromptCommand _)
    {
        var outcome = CommandOutcome.OK;
        if (State.Preferences.WidgetSettings.ShouldDisplayFollowFeaturedStoriesPrompt)
            outcome = outcome with { DomainEvent = new FeaturedStoriesTopicPromptDismissed(_userId) };
        return Task.FromResult(outcome);
    }

    public Task<CommandOutcome> Handle(DismissStoryPromptCommand _)
    {
        var outcome = CommandOutcome.OK;
        if (State.Preferences.WidgetSettings.ShouldDisplayStoryPrompt)
            outcome = outcome with { DomainEvent = new StoryPromptDismissed(_userId) };
        return Task.FromResult(outcome);
    }

    public Task<CommandOutcome> Handle(DismissRecipePromptCommand _)
    {
        var outcome = CommandOutcome.OK;
        if (State.Preferences.WidgetSettings.ShouldDisplayRecipePrompt)
            outcome = outcome with { DomainEvent = new RecipePromptDismissed(_userId) };
        return Task.FromResult(outcome);
    }

    public Task<CommandOutcome> Handle(DismissRecipeToolTipPromptCommand _)
    {
        var outcome = CommandOutcome.OK;
        if (State.Preferences.WidgetSettings.ShouldDisplayRecipeToolTipPrompt)
            outcome = outcome with { DomainEvent = new RecipeToolTipPromptDismissed(_userId) };
        return Task.FromResult(outcome);
    }

    public Task<CommandOutcome> Handle(DismissGroupPromptCommand _)
    {
        var outcome = CommandOutcome.OK;
        if (State.Preferences.WidgetSettings.ShouldDisplayGroupPrompt)
            outcome = outcome with { DomainEvent = new GroupPromptDismissed(_userId) };
        return Task.FromResult(outcome);
    }

    public Task<CommandOutcome> Handle(DismissQuickViewModalToolTipCommand _)
    {
        var outcome = CommandOutcome.OK;
        if (State.Preferences.WidgetSettings.ShouldDisplayQuickViewModalToolTip)
            outcome = outcome with { DomainEvent = new QuickViewModalToolTipDismissed(_userId) };
        return Task.FromResult(outcome);
    }

    public Task<CommandOutcome> Handle(DismissSearchPromptCommand _)
    {
        var outcome = CommandOutcome.OK;
        if (State.Preferences.WidgetSettings.ShouldDisplaySearchPrompt)
            outcome = outcome with { DomainEvent = new SearchPromptDismissed(_userId) };
        return Task.FromResult(outcome);
    }

    public Task<CommandOutcome> Handle(DismissFreeTrialPromptCommand _)
    {
        var outcome = CommandOutcome.OK;
        if (State.Preferences.WidgetSettings.ShouldDisplayFreeTrialPrompt)
            outcome = outcome with { DomainEvent = new FreeTrialPromptDismissed(_userId) };
        return Task.FromResult(outcome);
    }

    internal async Task<CommandOutcome> Handle(CreateFreeLDSAccountCommand _)
    {
        if (State.AccountSource != AccountSource.FreeLDSAccount)
        {
            return CommandOutcome.Error("not_eligible_for_freeldsaccount", "User is not eligible for Free LDS account");
        }

        return await AssignFreeLDSSubscription();
    }

    internal async Task<CommandOutcome> Handle(AssignFreeLDSSubscriptionCommand _) =>
        // TODO: Add the condition to validate whether user have church membership or not
        await AssignFreeLDSSubscription();

    public async Task<BasicUserInformation> GetUserDetails()
    {
        var userDetails = await GetUserDetailsFromAuth0(State.Auth0UserId);

        return new BasicUserInformation
        {
            UserId = UserId.ToString(),
            Auth0Id = State.Auth0UserId,
            EmailAddress = userDetails.Email,
            Firstname = userDetails.UserMetadata?.FirstName?.Value,
            Lastname = userDetails.UserMetadata?.LastName?.Value
        };
    }

    private async Task<Auth0.ManagementApi.Models.User> GetUserDetailsFromAuth0(string auth0UserId) => await _authProviderService.GetUserInfoFromAuth0(auth0UserId);

    private static RepresentingPersonProfileImageUpdateInNewTreeRequested RepresentingPersonProfileImageUpdateInNewTreeRequestedFrom(
        UserId userId, PersonId personId) => new(userId, personId);

    private static List<string> BuildPromptDisplayOrder()
    {
        var promptList = typeof(WidgetDisplayOrder).GetFields().Select(z => z.Name).ToList();
        return promptList.OrderBy(x => RandomNumberGenerator.GetInt32(0, promptList.Count)).ToList();
    }

    private static RepresentingPersonProfileImageUpdateRequested RepresentingPersonProfileImageUpdateRequestedFrom(
        MediaId mediaId, string profileImageUrl, HashSet<PersonId> representingPersons) => new(mediaId, profileImageUrl, representingPersons);

    private async Task<CommandOutcome> AssignFreeLDSSubscription()
    {
        var RequestContextNAUserId = Convert.ToString(RequestContext.Get("NAUserId")) ?? string.Empty;
        var RecurlyUsername = Convert.ToString(RequestContext.Get("RecurlyUsername"));
        var SubscriptionStatus = Convert.ToString(RequestContext.Get("SubscriptionStatus"));
        var userInformation = await GetUserDetails();

        if ((String.IsNullOrWhiteSpace(RequestContextNAUserId) || RequestContextNAUserId == "0") && string.IsNullOrWhiteSpace(RecurlyUsername))
        {
            //Request NA api to create an account and get an account code
            var account = await _subscriptionDataService.CreateFreeUser(userInformation);
            var NAUserId = account?.UserId ?? 0;

            if (NAUserId > 0)
            {
                await _recurlyService.CreateAccount(NAUserId.ToString(), token: string.Empty, userInformation);
                // create subscription
                if ((await _recurlyService.CreateSubscription(NAUserId.ToString(), ["19681"], string.Empty)).SubscriptionIds.Count != 0)
                {
                    return CommandOutcome.OK with
                    {
                        DomainEvent = new FreeLDSAccountCreated(new UserId(UserId), userInformation.Auth0Id)
                    };
                }
            }
            else
            {
                return CommandOutcome.Error("freeldsaccount_creation_failed", "FreeLDSAccount creation failed");
            }
        }
        else if (!string.IsNullOrWhiteSpace(RecurlyUsername) && !string.IsNullOrWhiteSpace(SubscriptionStatus) && SubscriptionStatus.ToLower().Equals("expired"))
        {
            var subscriptions = (await _recurlyService.CreateSubscription(RequestContextNAUserId, ["19681"], string.Empty)).SubscriptionIds;
            if (subscriptions.Count != 0)
            {
                return CommandOutcome.OK with
                {
                    DomainEvent = new FreeLDSAccountCreated(new UserId(UserId), userInformation.Auth0Id)
                };
            }
        }
        else if (!string.IsNullOrWhiteSpace(RequestContextNAUserId) && string.IsNullOrWhiteSpace(RecurlyUsername))
        {
            await _recurlyService.CreateAccount(RequestContextNAUserId, token: string.Empty, userInformation);

            var subscriptions = (await _recurlyService.CreateSubscription(RequestContextNAUserId, ["19681"], string.Empty)).SubscriptionIds;
            if (subscriptions.Count != 0)
            {
                return CommandOutcome.OK with
                {
                    DomainEvent = new FreeLDSAccountCreated(new UserId(UserId), userInformation.Auth0Id)
                };
            }
        }

        return CommandOutcome.Error("freeldsaccount_subscription_creation_failed", "Failed to create freeldsaccount subscription.");
    }
    private async Task UpdateUserCreatedAtFromAuth0()
    {
        var auth0UserInfo = await GetUserDetailsFromAuth0(State.Auth0UserId);
        if (auth0UserInfo?.CreatedAt != null)
            _createdAt = auth0UserInfo.CreatedAt.Value;
    }

    public async Task<DateTime?> GetUserCreationDate()
    {
        if (_createdAt == null)
            await UpdateUserCreatedAtFromAuth0();
        return _createdAt;
    }
}