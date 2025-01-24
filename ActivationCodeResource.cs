using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Orleans;
using Orleans.Runtime;

using WA.CommandHandler.CommandHandlers.ActivationCodes;
using WA.Data.RestService.Interface;
using WA.Domain.ActivationCodes;
using WA.Domain.ActivationCodes.Command;
using WA.Domain.ActivationCodes.Event;
using WA.Domain.Common.Command;
using WA.Domain.Common.Enumeration;
using WA.Domain.TypedIdentity;
using WA.EventPersistence.Interfaces.Store;
using WA.GrainInterface.Read;
using WA.GrainInterface.Write.ActivationCode;
using WA.Sagas;
using WA.Shared.Configuration;
using WA.Shared.Constant;
using WA.Shared.DTO;
using WA.Shared.ServiceInterface;

using User = Auth0.ManagementApi.Models.User;

namespace WA.CommandHandler.Grain;

public class ActivationCodeResource(
    ISagaCoordinator sagaCoordinator,
    ILogger<ActivationCodeResource> logger,
    IEventStore eventStore,
    IRecurlyService recurlyService,
    IAuthProviderService authProviderService,
    ISubscriptionDataService subscriptionDataService,
    IOptionsMonitor<ApplicationOptions> appOptions)
    : EventSourcedGrain<ActivationCode, ActivationCodeState>(sagaCoordinator, logger, eventStore, appOptions), IActivationCodeWrite, IActivationCodeRead
{

    private readonly IRecurlyService _recurlyService = recurlyService;
    private readonly ISubscriptionDataService _subscriptionDataService = subscriptionDataService;
    private readonly IAuthProviderService _authProviderService = authProviderService;
    private readonly PlansDetails _plansDetails = new();
    private readonly ILogger<ActivationCodeResource> _logger = logger;

    public override async Task OnActivateAsync(CancellationToken cancellationToken) => await base.OnActivateAsync(cancellationToken);

    public Task<CommandOutcome> Handle(GenerateActivationCodeCommand generateActivationCodeCommand)
    {
        if (!string.IsNullOrEmpty(State.ActivationCodeId))
            return Task.FromResult(CommandOutcome.Error("activation_code_exists", $"Cannot create a code with ID {State.ActivationCodeId}; it already exists."));

        return Task.FromResult(CommandOutcome.OK with
        {
            DomainEvent = new ActivationCodeGenerated(
                this.GetPrimaryKeyString(),
                generateActivationCodeCommand.ItemCode,
                DateTime.UtcNow)
        });
    }

    public Task<CommandOutcome> Handle(ReserveActivationCodeCommand reserveActivationCodeCommand) => new ReserveActivationCodeCommandHandler(State, GrainFactory).Handle(reserveActivationCodeCommand);

    public Task<CommandOutcome> Handle(ReserveBatchActivationCodeCommand reserveActivationCodeCommand) => new ReserveBatchActivationCodeCommandHandler(State, GrainFactory).Handle(reserveActivationCodeCommand);

    public async Task<CommandOutcome> Handle(RedeemActivationCodeCommand _)
    {
        if (State?.ActivationCodeId == null)
            return CommandOutcome.Error(StatusCodes.Status404NotFound.ToString(), "This activation code is not valid. Please check and try again");

        if (State.Status.Equals(ActivationCodeStatus.Cancelled))
            return CommandOutcome.Error(StatusCodes.Status400BadRequest.ToString(), "This activation code has already been cancelled.");

        if (State.Status.Equals(ActivationCodeStatus.Redeemed))
            return CommandOutcome.Error(StatusCodes.Status400BadRequest.ToString(), "This activation code has already been redeemed.");

        var item = await _recurlyService.GetItem(State.ItemCode);
        var planCodes = item.CustomFields.Find(x => x.Name == RecurlyDefaults.MappedPlanCodes)?.Value?.Replace(" ", "").Split(',') ?? [];
        var couponCode = item.CustomFields.Find(x => x.Name == RecurlyDefaults.MappedCouponCode)?.Value ?? string.Empty;

        var requestContextNAUserId = Convert.ToString(RequestContext.Get("NAUserId"));
        var recurlyUsername = Convert.ToString(RequestContext.Get("RecurlyUsername"));

        var userInformation = await GetUserDetails(UserId);
        _logger.LogInformation("RedeemActivationCodeCommand: userInformation {NAUserId}, ItemCode {itemCode} and ActivationCode {activationCode}", userInformation.UserId, State.ItemCode, State.ActivationCodeId);

        if (userInformation == null)
        {
            return CommandOutcome.Error("user_information_not_found", "User information not found.");
        }

        var basicUserInformation = MapUserBasicInformation(userInformation);
        var NAUserId = string.Empty;

        if (requestContextNAUserId == null || (int.TryParse(requestContextNAUserId, out var naUserId) && naUserId == 0))
        {
            var account = await _subscriptionDataService.CreateFreeUser(basicUserInformation);

            if (account?.UserId > 0)
            {
                NAUserId = account.UserId.ToString();
                _logger.LogInformation("RedeemActivationCodeCommand: account {accountCode}, ItemCode {itemCode} and ActivationCode {activationCode}", account, State.ItemCode, State.ActivationCodeId);
            }
            else
            {
                return CommandOutcome.Error("user_creation_failed", account?.ErrorMessage);
            }
        }
        else
        {
            NAUserId = requestContextNAUserId;
        }

        if (!string.IsNullOrEmpty(NAUserId) && string.IsNullOrEmpty(recurlyUsername))
        {
            await _recurlyService.CreateAccount(NAUserId, token: string.Empty, basicUserInformation);
            _logger.LogInformation("RedeemActivationCodeCommand: account created for {NAUserId}, ItemCode {itemCode} and ActivationCode {activationCode}", NAUserId, State.ItemCode, State.ActivationCodeId);
        }
        else
        {
            _logger.LogInformation("RedeemActivationCodeCommand: account already created for {NAUserId}, ItemCode {itemCode} and ActivationCode {activationCode}", NAUserId, State.ItemCode, State.ActivationCodeId);
        }

        if (!string.IsNullOrWhiteSpace(couponCode))
        {
            var account = await _recurlyService.GetAccount(NAUserId);
            await _recurlyService.CreateCouponRedemption(account.Id, couponCode);
        }

        foreach (var newPlanId in planCodes)
        {
            var newPlanType = SubscriptionPlans.FromString(newPlanId);
            if (!string.IsNullOrEmpty(newPlanType) && newPlanType.Equals(SubscriptionPlans.BookStudio.Value))
                //studio plan
                await ManageStudioPlan(NAUserId, newPlanId);
            else
                //content plan
                await ManageContentPlan(NAUserId, newPlanId, newPlanType);
        }

        if (planCodes.Length > 0)
            return GenerateActivationCodeRedeemedOutcome();

        return CommandOutcome.Error("bad_request", $"Cannot redeem a code with ID {State.ActivationCodeId}; Bad request.");
    }

    public async Task<CommandOutcome> Handle(CancelActivationCodeCommand _) => await new CancelActivationCodeCommandHandler(State, GrainFactory).Handle(new CancelActivationCodeCommand());

    #region Private Methods
    private async Task ManageStudioPlan(string NAUserId, string newPlanId)
    {
        var bookPlanId = Convert.ToString(RequestContext.Get("BookPlanId"));
        var bookSubscriptionId = Convert.ToString(RequestContext.Get("BookRecurlySubscriptionUuid"));
        var bookEndDate = Convert.ToString(RequestContext.Get("BookEndDate"));

        DateTime.TryParse(bookEndDate, CultureInfo.CreateSpecificCulture("en-US"), out var currenttBookPlanEndDate);

        if (!string.IsNullOrEmpty(bookSubscriptionId) && !string.IsNullOrEmpty(bookPlanId) && !string.IsNullOrEmpty(bookEndDate) &&
             currenttBookPlanEndDate > DateTime.UtcNow)
        {
            _logger.LogInformation("RedeemActivationCodeCommand: ManageStudioPlan- DowngradeStudioPlan for NAUserId {NAUserId},newPlanId {newPlanId} and ActivationCode {activationCode}", NAUserId, newPlanId, State.ActivationCodeId);

            await DowngradeStudioPlan(bookPlanId, bookSubscriptionId, newPlanId, currenttBookPlanEndDate, isDowngrade: true, isScheduleDowngrade: true);
            _logger.LogInformation("RedeemActivationCodeCommand: ManageStudioPlan- DowngradeStudioPlan has been changed for NAUserId {NAUserId},newPlanId {newPlanId} and ActivationCode {activationCode}", NAUserId, newPlanId, State.ActivationCodeId);

        }
        else
        {
            _logger.LogInformation("RedeemActivationCodeCommand: ManageStudioPlan- AddStudioPlan for NAUserId {NAUserId},newPlanId {newPlanId} and ActivationCode {activationCode}", NAUserId, newPlanId, State.ActivationCodeId);
            await AddStudioPlan(NAUserId, newPlanId);
            _logger.LogInformation("RedeemActivationCodeCommand: ManageStudioPlan- AddStudioPlan has been changed for NAUserId {NAUserId},newPlanId {newPlanId} and ActivationCode {activationCode}", NAUserId, newPlanId, State.ActivationCodeId);

        }
    }

    private async Task AddStudioPlan(string NAUserId, string newPlanId)
    {
        var response = (await _recurlyService.CreateSubscription(NAUserId.ToString(), [newPlanId], planCouponCode: string.Empty)).SubscriptionIds;
        if (response.Count > 0)
        {
            await GetStudioUpdatedPlansDetails(planId: newPlanId, currentBookPlanEndDate: null);
        }
    }

    private async Task DowngradeStudioPlan(string bookPlanId, string bookSubscriptionId, string newPlanId, DateTime currentBookPlanEndDate, bool isDowngrade = false, bool isScheduleDowngrade = false) => await ChangeStudioPlan(bookPlanId, bookSubscriptionId, newPlanId, currentBookPlanEndDate, isDowngrade, isScheduleDowngrade);

    private async Task UpgradeContentPlan(string subscriptionId, string planId, string newPlanId, DateTime contentPlanEndDate, bool isDowngrade = false, bool isScheduleDowngrade = false, bool isFreeTrial = false) => await ChangeContentPlan(subscriptionId, planId, newPlanId, contentPlanEndDate, isDowngrade, isScheduleDowngrade, isFreeTrial);

    private async Task DowngradeContentPlan(string subscriptionId, string planId, string newPlanId, DateTime contentPlanEndDate, bool isDowngrade = false, bool isScheduleDowngrade = false) => await ChangeContentPlan(subscriptionId, planId, newPlanId, contentPlanEndDate, isDowngrade, isScheduleDowngrade);

    private async Task ChangeStudioPlan(string bookPlanId, string bookSubscriptionId, string newPlanId, DateTime currentBookPlanEndDate, bool isDowngrade = false, bool isScheduleDowngrade = false)
    {
        _logger.LogInformation("RedeemActivationCodeCommand: ChangeStudioPlan for bookPlanId {bookPlanId},bookSubscriptionId {bookSubscriptionId},newPlanId {newPlanId} and ActivationCode {activationCode}", bookPlanId, bookSubscriptionId, newPlanId, State.ActivationCodeId);

        await _recurlyService.ChangeUserSubscription(bookPlanId, newPlanId, await GetplanAmount(bookPlanId), await GetplanAmount(newPlanId), bookSubscriptionId, isDowngrade, isScheduleDowngrade);

        _logger.LogInformation("RedeemActivationCodeCommand: ChangeStudioPlan- plan has been changed for bookPlanId {bookPlanId},bookSubscriptionId {bookSubscriptionId},newPlanId {newPlanId} and ActivationCode {activationCode}", bookPlanId, bookSubscriptionId, newPlanId, State.ActivationCodeId);

        await GetStudioPreviousPlanDetails(bookSubscriptionId);
        await GetStudioUpdatedPlansDetails(newPlanId, currentBookPlanEndDate);
    }

    private async Task ManageContentPlan(string NAUserId, string newPlanId, string newPlanType)
    {
        var subscriptionStatus = Convert.ToString(RequestContext.Get("SubscriptionStatus"));
        var subscriptionId = Convert.ToString(RequestContext.Get("SubscriptionId"));
        var planId = Convert.ToString(RequestContext.Get("PlanId"));
        var endDate = Convert.ToString(RequestContext.Get("EndDate"));
        DateTime.TryParse(endDate, CultureInfo.CreateSpecificCulture("en-US"), out var contentPlanEndDate);
        bool.TryParse(Convert.ToString(RequestContext.Get("isFreeTrial")), out var isFreeTrial);

        var currentPlanType = SubscriptionPlans.FromString(planId);

        if (!string.IsNullOrWhiteSpace(subscriptionStatus) && subscriptionStatus.ToLower().Equals("cancelled"))
        {
            _logger.LogInformation("RedeemActivationCodeCommand: ManageContentPlan- AddContentPlan for canceled subscriber NAUserId {NAUserId},newPlanId {newPlanId} and ActivationCode {activationCode}", NAUserId, newPlanId, State.ActivationCodeId);
            await AddContentPlan(NAUserId, newPlanId, canceledSubscriptionId: subscriptionId, contentPlanEndDate);
            _logger.LogInformation("RedeemActivationCodeCommand: ManageContentPlan- AddContentPlan has been added for canceled subscriber NAUserId {NAUserId},newPlanId {newPlanId} and ActivationCode {activationCode}", NAUserId, newPlanId, State.ActivationCodeId);
        }
        else
        {
            if (!string.IsNullOrEmpty(subscriptionId) && !string.IsNullOrEmpty(planId) && !string.IsNullOrEmpty(endDate) &&
             contentPlanEndDate > DateTime.UtcNow)
            {
                if (isFreeTrial)
                {
                    _logger.LogInformation("RedeemActivationCodeCommand: ManageContentPlan- UpgradeContentPlan for NAUserId {NAUserId},newPlanId {newPlanId} and ActivationCode {activationCode}", NAUserId, newPlanId, State.ActivationCodeId);
                    await UpgradeContentPlan(subscriptionId, planId, newPlanId, contentPlanEndDate, isDowngrade: false, isScheduleDowngrade: false, true);
                    _logger.LogInformation("RedeemActivationCodeCommand: ManageContentPlan- UpgradeContentPlan plan has been changed for NAUserId {NAUserId},newPlanId {newPlanId} and ActivationCode {activationCode}", NAUserId, newPlanId, State.ActivationCodeId);
                }
                else
                {
                    if ((currentPlanType.Equals(SubscriptionPlans.Plus.Value) && newPlanType.Equals(SubscriptionPlans.Plus.Value)) ||
                      (currentPlanType.Equals(SubscriptionPlans.Ultimate.Value) && newPlanType.Equals(SubscriptionPlans.Ultimate.Value)) ||
                      (currentPlanType.Equals(SubscriptionPlans.Ultimate.Value) && newPlanType.Equals(SubscriptionPlans.Plus.Value)) ||
                      (currentPlanType.Equals(SubscriptionPlans.PlusNew.Value) && newPlanType.Equals(SubscriptionPlans.Plus.Value)) ||
                      (currentPlanType.Equals(SubscriptionPlans.UltimateNew.Value) && newPlanType.Equals(SubscriptionPlans.Ultimate.Value)) ||
                      (currentPlanType.Equals(SubscriptionPlans.UltimateNew.Value) && newPlanType.Equals(SubscriptionPlans.Plus.Value))
                      && !string.IsNullOrEmpty(newPlanId))
                    {
                        _logger.LogInformation("RedeemActivationCodeCommand: ManageContentPlan- DowngradeContentPlan for NAUserId {NAUserId},newPlanId {newPlanId} and ActivationCode {activationCode}", NAUserId, newPlanId, State.ActivationCodeId);

                        await DowngradeContentPlan(subscriptionId, planId, newPlanId, contentPlanEndDate, isDowngrade: true, isScheduleDowngrade: true);

                        _logger.LogInformation("RedeemActivationCodeCommand: ManageContentPlan- DowngradeContentPlan plan has been changed for NAUserId {NAUserId},newPlanId {newPlanId} and ActivationCode {activationCode}", NAUserId, newPlanId, State.ActivationCodeId);

                    }
                    else if ((currentPlanType.Equals(SubscriptionPlans.Plus.Value) && newPlanType.Equals(SubscriptionPlans.Ultimate.Value)) ||
                     (currentPlanType.Equals(SubscriptionPlans.PlusNew.Value) && newPlanType.Equals(SubscriptionPlans.Ultimate.Value)) ||
                     (currentPlanType.Equals(SubscriptionPlans.Essential.Value) && newPlanType.Equals(SubscriptionPlans.Plus.Value)) ||
                     (currentPlanType.Equals(SubscriptionPlans.Essential.Value) && newPlanType.Equals(SubscriptionPlans.Ultimate.Value))
                     && !string.IsNullOrEmpty(newPlanId))
                    {
                        _logger.LogInformation("RedeemActivationCodeCommand: ManageContentPlan- UpgradeContentPlan for NAUserId {NAUserId},newPlanId {newPlanId} and ActivationCode {activationCode}", NAUserId, newPlanId, State.ActivationCodeId);

                        await UpgradeContentPlan(subscriptionId, planId, newPlanId, contentPlanEndDate, isDowngrade: false, isScheduleDowngrade: false);

                        _logger.LogInformation("RedeemActivationCodeCommand: ManageContentPlan- UpgradeContentPlan plan has been changed for NAUserId {NAUserId},newPlanId {newPlanId} and ActivationCode {activationCode}", NAUserId, newPlanId, State.ActivationCodeId);

                    }
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(newPlanType)
                && (newPlanType.Equals(RecurlyDefaults.Plus)
                || newPlanType.Equals(SubscriptionPlans.PlusNew.Value)
                || newPlanType.Equals(RecurlyDefaults.Ultimate)
                || newPlanType.Equals(SubscriptionPlans.UltimateNew.Value)))
                {
                    _logger.LogInformation("RedeemActivationCodeCommand: ManageContentPlan- AddContentPlan for NAUserId {NAUserId},newPlanId {newPlanId} and ActivationCode {activationCode}", NAUserId, newPlanId, State.ActivationCodeId);
                    await AddContentPlan(NAUserId, newPlanId, canceledSubscriptionId: string.Empty, endDate: null);
                    _logger.LogInformation("RedeemActivationCodeCommand: ManageContentPlan- AddContentPlan has been added for NAUserId {NAUserId},newPlanId {newPlanId} and ActivationCode {activationCode}", NAUserId, newPlanId, State.ActivationCodeId);
                }
            }
        }
    }

    private async Task AddContentPlan(string NAUserId, string newPlanId, string canceledSubscriptionId = "", DateTime? endDate = null)
    {
        var response = (await _recurlyService.CreateSubscription(NAUserId.ToString(), [newPlanId], planCouponCode: string.Empty, canceledSubscriptionEndDate: endDate)).SubscriptionIds;
        if (response.Count > 0)
        {
            if (!string.IsNullOrEmpty(canceledSubscriptionId))
                await GetContentPreviousPlanDetails(canceledSubscriptionId, false);
            await GetContentUpdatedPlansDetails(newPlanId, currentContentPlanEndDate: endDate);
        }
    }

    private async Task ChangeContentPlan(string subscriptionId, string planId, string newPlanId, DateTime contentPlanEndDate, bool isDowngrade = false, bool isScheduleDowngrade = false, bool isFreeTrial = false)
    {
        if (!isDowngrade && !isScheduleDowngrade)
            contentPlanEndDate = DateTime.UtcNow;

        await _recurlyService.ChangeUserSubscription(planId, newPlanId, await GetplanAmount(planId), await GetplanAmount(newPlanId), subscriptionId, isDowngrade, isScheduleDowngrade);
        await GetContentPreviousPlanDetails(subscriptionId, isFreeTrial);
        await GetContentUpdatedPlansDetails(newPlanId, contentPlanEndDate);
    }

    private async Task GetStudioPreviousPlanDetails(string subscriptionId) => await PreviousPlanDetails(subscriptionId, hasStudioPlan: true);

    private async Task GetContentPreviousPlanDetails(string subscriptionId, bool isFreeTrial = false) => await PreviousPlanDetails(subscriptionId, hasStudioPlan: false, isFreeTrial);

    private async Task PreviousPlanDetails(string subscriptionId, bool hasStudioPlan = false, bool isFreeTrial = false)
    {
        if (!string.IsNullOrEmpty(subscriptionId))
        {
            var currentSubscriptionResponse = await _recurlyService.GetSubscription(subscriptionId);
            if (currentSubscriptionResponse != null)
            {
                var existingPlan = _plansDetails.PreviousPlans.FirstOrDefault(p => p.PlanName.Equals(currentSubscriptionResponse.Plan.Name));
                var endDate = (currentSubscriptionResponse?.ExpiresAt == null ? currentSubscriptionResponse?.CurrentPeriodEndsAt : currentSubscriptionResponse?.ExpiresAt)?.ToString("MMMM dd, yyyy", CultureInfo.CreateSpecificCulture("en-US"));
                if (existingPlan != null)
                {
                    existingPlan.EndDate = endDate;
                }
                else
                {
                    _plansDetails.PreviousPlans.Add(
                   new()
                   {
                       PlanName = currentSubscriptionResponse.Plan.Name,
                       EndDate = endDate,
                       HasStudioPlan = hasStudioPlan,
                       IsFreeTrial = isFreeTrial,
                       PlanType = SubscriptionPlans.FromString(currentSubscriptionResponse.Plan.Code)
                   });
                }
            }
        }
    }

    private async Task GetStudioUpdatedPlansDetails(string planId, DateTime? currentBookPlanEndDate) => await UpdatedPlanDetails(planId, currentBookPlanEndDate, hasStudioBookPlan: true);

    private async Task GetContentUpdatedPlansDetails(string planId, DateTime? currentContentPlanEndDate) => await UpdatedPlanDetails(planId, currentContentPlanEndDate, hasStudioBookPlan: false);

    private async Task UpdatedPlanDetails(string planId, DateTime? currentPlanEndDate, bool hasStudioBookPlan)
    {
        if (!string.IsNullOrEmpty(planId))
        {
            var updatedSubscriptionResponse = await _recurlyService.GetPlan(planId);
            var intervalLength = updatedSubscriptionResponse.TrialLength ?? 0;
            if (updatedSubscriptionResponse != null)
            {
                var existingPlan = _plansDetails.UpdatedPlans.FirstOrDefault(p => p.PlanName.Equals(updatedSubscriptionResponse.Name));
                if (existingPlan != null)
                {
                    existingPlan.StartDate = (currentPlanEndDate != null ? currentPlanEndDate.Value : DateTime.UtcNow).ToString("MMMM dd, yyyy", CultureInfo.CreateSpecificCulture("en-US"));
                    existingPlan.EndDate = (currentPlanEndDate != null ? currentPlanEndDate.Value : DateTime.UtcNow).AddMonths(intervalLength).ToString("MMMM dd, yyyy", CultureInfo.CreateSpecificCulture("en-US"));
                }
                else
                {
                    _plansDetails.UpdatedPlans.Add(
                                new()
                                {
                                    PlanName = updatedSubscriptionResponse.Name,
                                    StartDate = (currentPlanEndDate != null ? currentPlanEndDate.Value : DateTime.UtcNow).ToString("MMMM dd, yyyy", CultureInfo.CreateSpecificCulture("en-US")),
                                    EndDate = (currentPlanEndDate != null ? currentPlanEndDate.Value : DateTime.UtcNow).AddMonths(intervalLength).ToString("MMMM dd, yyyy", CultureInfo.CreateSpecificCulture("en-US")),
                                    HasStudioPlan = hasStudioBookPlan,
                                    PlanType = SubscriptionPlans.FromString(planId)
                                }
                                );
                }
            }
        }
    }

    private async Task<float> GetplanAmount(string planId)
    {
        var plan = await _recurlyService.GetPlan(planId);
        var amount = plan.Currencies?.FirstOrDefault()?.UnitAmount;
        return (float)(amount ?? 0);
    }

    private CommandOutcome GenerateActivationCodeRedeemedOutcome() =>
        CommandOutcome.OK with
        {
            DomainEvent = new ActivationCodeRedeemed(this.GetPrimaryKeyString(), new UserId(UserId), DateTime.UtcNow, DateTime.UtcNow)
        };

    private BasicUserInformation MapUserBasicInformation(User? userInformation) =>
        new()
        {
            UserId = UserId.ToString(),
            Auth0Id = userInformation?.UserId ?? string.Empty,
            EmailAddress = userInformation?.Email ?? string.Empty,
            Firstname = userInformation?.UserMetadata?.FirstName ?? string.Empty,
            Lastname = userInformation?.UserMetadata?.LastName ?? string.Empty
        };

    private async Task<Auth0.ManagementApi.Models.User> GetUserDetails(Guid userId)
    {
        _logger.LogInformation("GetUserDetails: user {userId}", userId);
        var userReader = GrainFactory.GetGrain<IUserRead>(userId);
        var (userDetail, _) = await userReader.Get();

        _logger.LogInformation("GetUserDetails: userDetails {Auth0UserId}", userDetail.Auth0UserId);
        return await _authProviderService.GetUserInfoFromAuth0(userDetail.Auth0UserId);
    }
    #endregion

    #region Get methods
    public Task<PlansDetails> GetPlansDetails() => Task.FromResult(_plansDetails);
    #endregion
}
