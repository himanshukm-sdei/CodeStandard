using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mail;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;

using Auth0.ManagementApi.Models;

using Mapster;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.FeatureManagement;
using Microsoft.WindowsAzure.Storage;

using Orleans;
using Orleans.Runtime;

using Serilog;

using WA.API.Filter;
using WA.Client.DTO;
using WA.Data.RestService.Interface;
using WA.Domain.Common.Command.Entity;
using WA.Domain.Common.Enumeration;
using WA.Domain.Tree.Command;
using WA.Domain.TypedIdentity;
using WA.Domain.User.Command;
using WA.Domain.User.Query;
using WA.Domain.UserBehaviour.Command;
using WA.Domain.UserCoupons.Command;
using WA.Domain.UserCoupons.Enum;
using WA.GrainInterface.ExternalAuthentication;
using WA.GrainInterface.Query;
using WA.GrainInterface.Read;
using WA.GrainInterface.Write;
using WA.MailService.Interface;
using WA.Shared;
using WA.Shared.Configuration;
using WA.Shared.Constant;
using WA.Shared.DTO;
using WA.Shared.ServiceInterface;

namespace WA.API.Command.Controllers;

[Route("api/[controller]")]
[ApiController]
public class UserController(
    IClusterClient client,
    ILogger<UserController> logger,
    IOptionsMonitor<ApplicationOptions> appOptions,
    IHttpContextAccessor contextAccessor,
    ISendMailService sendMailService,
    IAuthProviderService authProviderService,
    ISubscriptionDataService subscriptionDataService,
    IAzureTableUserService azureTableUserService,
    IDateTimeProvider dateTimeProvider,
    IFeatureManager featureManager,
    ISubscriptionQueryHandler subscriptionQueryHandler,
    IRecurlyService recurlyService) : BaseController(contextAccessor, appOptions)
{
    private readonly IAuthProviderService _authProviderService = authProviderService;
    private readonly IAzureTableUserService _azureTableUserService = azureTableUserService;
    private readonly IClusterClient _client = client;
    private readonly IDateTimeProvider _dateTimeProvider = dateTimeProvider;
    private readonly IFeatureManager _featureManager = featureManager;
    private readonly ILogger<UserController> _logger = logger;
    private readonly ISendMailService _sendMailService = sendMailService;
    private readonly ISubscriptionDataService _subscriptionDataService = subscriptionDataService;
    private readonly ISubscriptionQueryHandler _subscriptionQueryHandler = subscriptionQueryHandler;
    private readonly IRecurlyService _recurlyService = recurlyService;

    private List<AccessDto> _accessCodes;

    [Route("deleteuserprofileimage/{userId}")]
    [HttpDelete]
    public async Task DeleteUserProfileImage(Guid userId)
    {
        var userWriter = _client.GetGrain<IUserWrite>(userId);
        var deleteUserProfileImageCommand = new DeleteUserProfileImageCommand();
        _ = await userWriter.Receive(deleteUserProfileImageCommand);
    }

    [HttpDelete("deleteaccount/{userId}")]
    public async Task<IActionResult> DeleteUserAccount(Guid userId)
    {
        var userWriter = _client.GetGrain<IUserWrite>(userId);
        var deleteUserAccountCommand = new DeleteUserAccountCommand();
        var outcome = await userWriter.Receive(deleteUserAccountCommand);
        if (outcome.Failed)
            return StatusCode(StatusCodes.Status400BadRequest, outcome.Problems);
        return StatusCode(StatusCodes.Status200OK);
    }

    [HttpPut("undodeleteaccountrequest/{userId}")]
    public async Task<IActionResult> UndoDeleteAccountRequest(Guid userId)
    {
        var userWriter = _client.GetGrain<IUserWrite>(userId);
        var undoDeleteUserAccountCommand = new UndoDeleteUserAccountCommand();
        var outcome = await userWriter.Receive(undoDeleteUserAccountCommand);
        if (outcome.Failed)
            return StatusCode(StatusCodes.Status400BadRequest, outcome.Problems);
        return StatusCode(StatusCodes.Status200OK);
    }

    [Route("nauser")]
    [HttpPost]
    public async Task Post([FromBody] UserDto userDto)
    {
        var userWriter = _client.GetGrain<IUserWrite>(UserId);
        var migrateNAUser = userDto.Adapt<MigrateNAUserToCommand>();
        _ = await userWriter.Receive(migrateNAUser);
    }

    [Route("addrecentlyviewedpeople/{treeId}/{treePersonId}")]
    [HttpPost]
    public async Task<IActionResult> Post(Guid treeId, Guid treePersonId)
    {
        var userWriter = _client.GetGrain<IUserRecentlyViewedWrite>(UserId);
        var outcome = await userWriter.Receive(new RegisterViewedPersonCommand(treeId, treePersonId));
        if (outcome.Failed) return StatusCode(StatusCodes.Status400BadRequest, outcome.Problems);
        return StatusCode(StatusCodes.Status200OK);
    }

    [Route("createuser/{accountSource?}/{familySearchId?}")]
    [HttpPost]
    public async Task<IActionResult> CreateUser(string accountSource = "", string familySearchId = "")
    {
        try
        {
            Log.Information("Execution started of CreatedUser for UserId-{UserId}", UserId);

            var userWriter = _client.GetGrain<IUserWrite>(UserId);
            var outcome = await userWriter.Receive(new CreateUserCommand(AccountSource.FromString(accountSource), familySearchId));

            if (outcome.Failed) return StatusCode(StatusCodes.Status202Accepted, outcome.Problems);

            return StatusCode(StatusCodes.Status200OK);
        }
        catch (Exception ex)
        {
            Log.Information(ex, "Error while executing CreateUser for UserId-{UserId} : {Ex}", UserId, ex);
            return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    [Route("migrateuser")]
    [HttpPost]
    public async Task<IActionResult> MigrateUser()
    {
        try
        {
            var userWriter = _client.GetGrain<IUserWrite>(UserId);
            var outcome = await userWriter.Receive(new MigrateUserToAuth0Command());

            if (outcome.Failed) return StatusCode(StatusCodes.Status202Accepted, outcome.Problems);

            return StatusCode(StatusCodes.Status200OK);
        }
        catch (Exception ex)
        {
            Log.Information(ex, "Error while executing MigrateUser for UserId-{UserId} : {Ex}", UserId, ex);
            return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    [Route("updateuser")]
    [HttpPost]
    public async Task<IActionResult> UpdateUser([FromBody] UserDto userDto)
    {
        var errorMessage = string.Empty;

        var claims = ((ClaimsIdentity)ContextAccessor.HttpContext!.User.Identity)?.Claims;
        if (claims != null)
        {
            var userGrain = _client.GetGrain<IUserRead>(userDto.UserId);
            var (userInfo, _) = await userGrain.Get();

            errorMessage = await UpdateAuth0UserProfileByUserId(userInfo.Auth0UserId, userDto);
            if (string.IsNullOrWhiteSpace(errorMessage))
            {
                var userWriter = _client.GetGrain<IUserWrite>(UserId);
                _ = await userWriter.Receive(new UpdateUserProfileCommand(userDto.Email, userDto.GivenName.Trim(), userDto.Surname.Trim()));
                return StatusCode(StatusCodes.Status200OK);
            }
        }

        return StatusCode(StatusCodes.Status500InternalServerError, errorMessage);
    }

    [Route("updateuseremail")]
    [HttpPost]
    public async Task<IActionResult> UpdateUserEmail([FromBody] UserDto userDto)
    {
        var errorMessage = IsValidEmail(userDto.Email);
        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            _logger.LogError("Can not update email address for UserId: {UserId}. Error: {ErrorMessage}", userDto.UserId, errorMessage);
            return StatusCode(StatusCodes.Status500InternalServerError, errorMessage);
        }

        var claims = ((ClaimsIdentity)ContextAccessor.HttpContext!.User.Identity)?.Claims;
        if (claims != null)
        {
            var userGrain = _client.GetGrain<IUserRead>(userDto.UserId);
            var (userInfo, _) = await userGrain.Get();

            errorMessage = await UpdateAuth0UserEmailByUserId(userDto.UserId.ToString(), userInfo.Auth0UserId, userDto.Email);
            if (string.IsNullOrWhiteSpace(errorMessage))
            {
                var userWriter = _client.GetGrain<IUserWrite>(UserId);
                _ = await userWriter.Receive(new UpdateUserProfileCommand(userDto.Email, userDto.GivenName, userDto.Surname));
                return StatusCode(StatusCodes.Status200OK);
            }
        }

        return StatusCode(StatusCodes.Status302Found, errorMessage);
    }

    [Route("milopreferences")]
    [HttpPost]
    public async Task<IActionResult> SaveMiloPreferences([FromBody] MiloDto miloDto)
    {
        var userWriter = _client.GetGrain<IUserWrite>(UserId);
        var outcome = await userWriter.Receive(miloDto.Adapt<AddMiloPreferencesCommand>());
        if (outcome.Failed)
            return StatusCode(StatusCodes.Status400BadRequest, outcome.Problems);
        return StatusCode(StatusCodes.Status200OK);
    }

    [Route("milopreferencesupdate")]
    [HttpPost]
    public async Task<IActionResult> UpdateMiloPreferences([FromBody] UpdateMiloDto updateMiloDto)
    {
        var userWriter = _client.GetGrain<IUserWrite>(UserId);
        var outcome = await userWriter.Receive(updateMiloDto.Adapt<UpdateMiloPreferencesCommand>());
        if (outcome.Failed)
            return StatusCode(StatusCodes.Status400BadRequest, outcome.Problems);
        return StatusCode(StatusCodes.Status200OK);
    }

    [AllowAnonymous]
    [Route("verifyaccess")]
    [HttpPost]
    public async Task<string> VerifyAccess([FromBody] ShellDto shellDto)
    {
        _accessCodes ??= await DownloadAccessKeys();
        if (_accessCodes != null &&
            _accessCodes.Exists(item => item.AccessCode == shellDto.AccessCode &&
                                        DateTime.ParseExact(item.ExpiryDate, "dd/MM/yyyy", null).Date >= DateTime.Now.Date))
        {
            return "Success";
        }

        return "Failure";
    }

    [HttpPost]
    [Route("create-default-tree/{treeId:guid}/{homePersonId:guid}")]
    public async Task<IActionResult> CreateDefaultTree(Guid? treeId = null, Guid? homePersonId = null)
    {
        var auth0Details = await GetAuth0Details();

        treeId ??= Guid.NewGuid();
        homePersonId ??= Guid.NewGuid();

        string givenName = auth0Details.UserMetadata?.FirstName?.Value;
        string surname = auth0Details.UserMetadata?.LastName?.Value;

        var homePerson = new Person { PersonId = new PersonId(homePersonId.Value), GivenName = givenName ?? "Me", Surname = surname ?? "", Gender = Gender.None, TreeId = new TreeId(treeId.Value) };

        string birthYear = auth0Details.UserMetadata?.BirthYear?.Value;
        if (!string.IsNullOrWhiteSpace(birthYear)) homePerson = homePerson with { BirthDate = birthYear };

        var treeName = string.IsNullOrWhiteSpace(surname) ? "Family Tree" : $"{surname.Trim()} Family Tree";

        var createDefaultTreeCommand = new CreateDefaultTreeCommand(new TreeId(treeId.Value), treeName, new UserId(UserId), homePerson);

        var treeWriter = _client.GetGrain<ITreeWrite>(treeId.Value);
        var outcome = await treeWriter.Receive(createDefaultTreeCommand);

        if (outcome.Failed)
        {
            if (outcome.Problems[0].Code == "tree_exists") return StatusCode(StatusCodes.Status400BadRequest, "Tree already exists");
            return StatusCode(StatusCodes.Status500InternalServerError);
        }

        return Ok();
    }

    private async Task<User> GetAuth0Details()
    {
        var userRead = _client.GetGrain<IUserRead>(UserId);
        var (user, _) = await userRead.Get();
        return await _authProviderService.GetUserInfoFromAuth0(user.Auth0UserId);
    }

    [HttpPost("addrecentlyviewedtree/{treeId}")]
    public async Task<IActionResult> AddRecentlyViewedTree(Guid treeId)
    {
        var treeReader = _client.GetGrain<ITreeRead>(treeId);
        var (tree, _) = await treeReader.Get();
        var userWriter = _client.GetGrain<IUserRecentlyViewedWrite>(UserId);
        var outcome = await userWriter.Receive(new RegisterViewedTreeCommand(tree.UserId, tree.Id));
        if (outcome.Failed) return StatusCode(StatusCodes.Status400BadRequest, outcome.Problems);

        return StatusCode(StatusCodes.Status200OK);
    }

    [HttpPost("add-recently-viewed-group/{groupId}")]
    public async Task<IActionResult> AddRecentlyViewedGroup(Guid groupId)
    {
        var groupReader = _client.GetGrain<IGroupRead>(groupId);
        var (group, _) = await groupReader.Get();
        var userWriter = _client.GetGrain<IUserRecentlyViewedWrite>(UserId);
        var outcome = await userWriter.Receive(new RegisterViewedGroupCommand(group.OwnerId, group.Id));
        if (outcome.Failed)
        {
            return StatusCode(StatusCodes.Status400BadRequest, outcome.Problems);
        }

        return StatusCode(StatusCodes.Status200OK, "Successfully added group to Recently Viewed");
    }

    [HttpPost("add-recently-viewed-project/{projectId}")]
    public async Task<IActionResult> AddRecentlyViewedProject(Guid projectId)
    {
        var projectReader = _client.GetGrain<IProjectRead>(projectId);
        var (project, _) = await projectReader.Get();
        var userWriter = _client.GetGrain<IUserRecentlyViewedWrite>(UserId);
        var outcome = await userWriter.Receive(new RegisterViewedProjectCommand(project.OwnerId, project.Id));
        if (outcome.Failed)
        {
            return StatusCode(StatusCodes.Status400BadRequest, outcome.Problems);
        }

        return StatusCode(StatusCodes.Status200OK, "Successfully added project to Recently Viewed");
    }

    [HttpPut("marknotificationread/{notificationId}")]
    public async Task<IActionResult> MarkNotificationRead(Guid notificationId)
    {
        var userWriter = _client.GetGrain<IUserNotificationWrite>(UserId);
        var outcome = await userWriter.Receive(new MarkNotificationReadCommand(new NotificationId(notificationId)));
        if (outcome.Failed) return StatusCode(StatusCodes.Status400BadRequest, outcome.Problems);

        return StatusCode(StatusCodes.Status200OK);
    }

    [HttpPut("updatelasttimeusersawnotification")]
    public async Task<IActionResult> UpdateLastTimeUserSawNotification()
    {
        var userWriter = _client.GetGrain<IUserNotificationWrite>(UserId);
        var outcome = await userWriter.Receive(new UpdateLastTimeUserSawNotification());
        if (outcome.Failed) return StatusCode(StatusCodes.Status400BadRequest, outcome.Problems);

        return StatusCode(StatusCodes.Status200OK);
    }

    [Route("follow/{userId}")]
    [HttpPost]
    public async Task<IActionResult> FollowUser(Guid userId)
    {
        var userWriter = _client.GetGrain<IUserWrite>(UserId);
        var outcome = await userWriter.Receive(new FollowUserCommand(userId));
        if (outcome.Failed) return StatusCode(StatusCodes.Status400BadRequest, outcome.Problems);
        return StatusCode(StatusCodes.Status200OK);
    }

    [Route("unfollow/{userId}")]
    [HttpPost]
    public async Task<IActionResult> UnFollowUser(Guid userId)
    {
        var userWriter = _client.GetGrain<IUserWrite>(UserId);
        var outcome = await userWriter.Receive(new UnfollowUserCommand(userId));
        if (outcome.Failed) return StatusCode(StatusCodes.Status400BadRequest, outcome.Problems);
        return StatusCode(StatusCodes.Status200OK);
    }

    [Route("followtopic/{topicId}")]
    [HttpPost]
    public async Task<IActionResult> FollowTopic(Guid topicId)
    {
        var userWriter = _client.GetGrain<IUserWrite>(UserId);
        var outcome = await userWriter.Receive(new FollowTopicCommand(topicId));
        if (outcome.Failed) return StatusCode(StatusCodes.Status400BadRequest, outcome.Problems);
        return StatusCode(StatusCodes.Status200OK);
    }

    [Route("unfollowtopic/{topicId}")]
    [HttpPost]
    public async Task<IActionResult> UnFollowTopic(Guid topicId)
    {
        var userWriter = _client.GetGrain<IUserWrite>(UserId);
        var outcome = await userWriter.Receive(new UnfollowTopicCommand(topicId));
        if (outcome.Failed) return StatusCode(StatusCodes.Status400BadRequest, outcome.Problems);
        return StatusCode(StatusCodes.Status200OK);
    }

    [Route("ccpa")]
    [HttpPut]
    [AllowAnonymous]
    public async Task<IActionResult> CCPARequestForAdmin(CcpaInformationDto ccpaInformationDto)
    {
        var message = await _sendMailService.SendCCPAInformation(ccpaInformationDto);
        if (string.IsNullOrEmpty(message)) return StatusCode(StatusCodes.Status200OK);
        return StatusCode(StatusCodes.Status500InternalServerError, message);
    }

    [Route("subscriptioninfo")]
    [HttpPost]
    public async Task<IActionResult> SaveSubscriptionInfo(UserSubscriptionInfoDto userSubscriptionInfoDto)
    {
        var userSubscriptionInfoHandler = _client.GetGrain<IUserSubscriptionWrite>(UserId);
        var outcome = await userSubscriptionInfoHandler.Receive(userSubscriptionInfoDto.Adapt<SaveUserSubscriptionInfoCommand>());
        if (outcome.Failed)
            return StatusCode(StatusCodes.Status400BadRequest, outcome.Problems);
        return StatusCode(StatusCodes.Status200OK);
    }

    [Route("sessiongroupmemberinfo")]
    [HttpPost]
    public async Task<IActionResult> SaveSessionGroupMemberInfo(SessionGroupMemberInfoDto sessionGroupMemberInfoDto)
    {
        var groupMemberInfoHandler = _client.GetGrain<ISessionGroupMemberInfoWrite>(UserId);
        var outcome = await groupMemberInfoHandler.Receive(sessionGroupMemberInfoDto.Adapt<SaveSessionGroupMemberInfoCommand>());
        if (outcome.Failed)
            return StatusCode(StatusCodes.Status400BadRequest, outcome.Problems);
        return StatusCode(StatusCodes.Status200OK);
    }

    [Route("dismissTopicPrompt")]
    [HttpPost]
    public async Task<IActionResult> DismissTopicPrompt()
    {
        var userWriter = _client.GetGrain<IUserWrite>(UserId);
        return await IssueCommand(userWriter, new DismissFeaturedStoriesTopicPromptCommand());
    }

    [Route("dismissStoryPrompt")]
    [HttpPost]
    public async Task<IActionResult> DismissStoryPrompt()
    {
        var userWriter = _client.GetGrain<IUserWrite>(UserId);
        return await IssueCommand(userWriter, new DismissStoryPromptCommand());
    }

    [Route("dismissRecipePrompt")]
    [HttpPost]
    public async Task<IActionResult> DismissRecipePrompt()
    {
        var userWriter = _client.GetGrain<IUserWrite>(UserId);
        return await IssueCommand(userWriter, new DismissRecipePromptCommand());
    }

    [Route("dismissRecipeToolTipPrompt")]
    [HttpPost]
    public async Task<IActionResult> DismissRecipeToolTipPrompt()
    {
        var userWriter = _client.GetGrain<IUserWrite>(UserId);
        return await IssueCommand(userWriter, new DismissRecipeToolTipPromptCommand());
    }

    [Route("dismissGroupPrompt")]
    [HttpPost]
    public async Task<IActionResult> DismissGroupPrompt()
    {
        var userWriter = _client.GetGrain<IUserWrite>(UserId);
        return await IssueCommand(userWriter, new DismissGroupPromptCommand());
    }

    [Route("dismissSearchPrompt")]
    [HttpPost]
    public async Task<IActionResult> DismissSearchPrompt()
    {
        var userWriter = _client.GetGrain<IUserWrite>(UserId);
        return await IssueCommand(userWriter, new DismissSearchPromptCommand());
    }

    [Route("dismissFreeTrialPrompt")]
    [HttpPost]
    public async Task<IActionResult> DismissFreeTrialPrompt()
    {
        var userWriter = _client.GetGrain<IUserWrite>(UserId);
        return await IssueCommand(userWriter, new DismissFreeTrialPromptCommand());
    }

    [Route("updateStatusForOldUserFollowingFeatureTopic")]
    [AuthorizeAdmin]
    [HttpPost]
    public async Task<IActionResult> UpdateStatusForOldUserFollowingFeatureTopic()
    {
        var adminQuery = _client.GetGrain<IAdminQuery>(0);
        var batchIds = (await adminQuery.Receive<HashSet<Guid>>(new ListUsersFollowingFeaturedStoriesTopic())).ResultData;
        var batchUserIdsList = batchIds.Chunk(20);
        foreach (var batchUserIds in batchUserIdsList)
        {
            List<Task> tasks = [];
            foreach (var userId in batchUserIds)
            {
                var userWriter = _client.GetGrain<IUserWrite>(userId);
                tasks.Add(userWriter.Receive(new DismissFeaturedStoriesTopicPromptCommand()));
            }

            await Task.WhenAll(tasks);
            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        return StatusCode(StatusCodes.Status200OK);
    }

    [Route("updatePromptOrderForOldUsers")]
    [AuthorizeAdmin]
    [HttpPost]
    public async Task<IActionResult> UpdatePromptOrderForOldUsers()
    {
        var adminQuery = _client.GetGrain<IAdminQuery>(0);
        var batchIds = (await adminQuery.Receive<HashSet<Guid>>(new GetAllUserIds())).ResultData;
        var batchUserIdsList = batchIds.Chunk(20);
        foreach (var batchUserIds in batchUserIdsList)
        {
            List<Task> tasks = [];
            foreach (var userId in batchIds)
            {
                var userWriter = _client.GetGrain<IUserWrite>(userId);
                tasks.Add(userWriter.Receive(new InitializeUserPreferencesCommand()));
            }

            await Task.WhenAll(tasks);
            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        return StatusCode(StatusCodes.Status200OK);
    }

    [Route("dismissquickviewmodaltooltip")]
    [HttpPost]
    public async Task<IActionResult> DismissQuickViewModalToolTip()
    {
        var userWriter = _client.GetGrain<IUserWrite>(UserId);
        return await IssueCommand(userWriter, new DismissQuickViewModalToolTipCommand());
    }

    [HttpPost("create-subscription-user")]
    public async Task<IActionResult> CreateSubscriptionUser(CreateSubscriptionUserDto createSubscriptionUserDto)
    {
        if (await _featureManager.IsEnabledAsync(FeatureFlags.FF_MobileRecurlySupport))
        {
            var createSubscriptionUserCommand = createSubscriptionUserDto.Adapt<CreateSubscriptionUserCommand>() with { UserId = UserId.ToString() };

            if (UserHasAuth0Id(out var auth0Id))
                createSubscriptionUserCommand = createSubscriptionUserCommand with { Auth0Id = auth0Id };

            Log.Information("Auth0 and Userid: {Auth0} and {UserId}", createSubscriptionUserCommand.Auth0Id, UserId);

            var recurlyResponse = await _subscriptionDataService.CreateUser(createSubscriptionUserCommand);

            if (recurlyResponse is null || !recurlyResponse.Success)
                return StatusCode(StatusCodes.Status400BadRequest, recurlyResponse);

            return new JsonResult(recurlyResponse);
        }

        return StatusCode(StatusCodes.Status501NotImplemented);
    }

    [HttpPost("subscribe")]
    public async Task<IActionResult> Subscribe(SubscriptionInfoDto subscriptionInfoDto)
    {
        var userSubscriptionCommand = subscriptionInfoDto.Adapt<UserSubscriptionCommand>();

        if (UserHasAuth0Id(out var auth0Id))
            userSubscriptionCommand.UserInformation.Auth0Id = auth0Id;

        Log.Information("Auth0 and Userid: {Auth0} and {UserId}", userSubscriptionCommand.UserInformation.Auth0Id, UserId);

        subscriptionInfoDto.UserInformation.UserId = UserId.ToString();
        var subscriptionResponse = await _subscriptionDataService.Subscribe(userSubscriptionCommand);
        if (!string.IsNullOrEmpty(subscriptionResponse?.ErrorMessage)) return StatusCode(StatusCodes.Status400BadRequest, subscriptionResponse);

        var subscription = await _recurlyService.GetSubscription(subscriptionResponse.RecurlySubscriptionUuid);
        var invoice = await _recurlyService.GetInvoice(subscription.ActiveInvoiceId);
        subscriptionResponse.RecurlyTransactionUuid = invoice.Transactions.Select(x => x.Uuid).FirstOrDefault();

        return new JsonResult(subscriptionResponse);
    }

    [HttpPost("cancelsubscription")]
    public async Task<IActionResult> CancelSubscription(CancelSubscriptionDto cancelSubscriptionDto)
    {
        var cancelSubscriptionResponse = await _subscriptionDataService.CancelSubscription(cancelSubscriptionDto.Adapt<CancelSubscriptionCommand>());
        if (cancelSubscriptionResponse is null || !cancelSubscriptionResponse.Success)
            return StatusCode(StatusCodes.Status400BadRequest, cancelSubscriptionResponse);

        var userCouponReader = _client.GetGrain<IUserCouponRead>(UserId);
        var (userCoupon, _) = await userCouponReader.Get();
        //this will work only for the trail plan(PendingActivation)
        if (!await userCouponReader.IsNew() && userCoupon.GetEffectiveStatus(_dateTimeProvider).Equals(CouponStatus.PendingActivation))
        {
            //revoke coupon that are in pending state
            var userCouponWrite = _client.GetGrain<IUserCouponWrite>(UserId);
            _ = await userCouponWrite.Receive(new RevokePendingCouponCommand());
        }

        return new JsonResult(cancelSubscriptionResponse);
    }

    [HttpPost("remove-pending-subscription")]
    public async Task<IActionResult> RemovePendingSubscription(string subscriptionId)
    {
        var subscriptionResponse = await _subscriptionDataService.RemovePendingSubscription(subscriptionId);
        if (subscriptionResponse is null || !subscriptionResponse.Success) return StatusCode(StatusCodes.Status400BadRequest, subscriptionResponse);
        return new JsonResult(subscriptionResponse);
    }

    [HttpPost("reactivate-subscription")]
    public async Task<IActionResult> ReactivateCanceledSubscription(string subscriptionId)
    {
        var subscriptionResponse = await _subscriptionDataService.ReactivateCanceledSubscription(subscriptionId);
        if (subscriptionResponse is null || !subscriptionResponse.Success) return StatusCode(StatusCodes.Status400BadRequest, subscriptionResponse);
        return new JsonResult(subscriptionResponse);
    }

    [HttpPost("changesubscription")]
    public async Task<IActionResult> ChangeSubscription(ChangeSubscriptionDto changeSubscriptionDto)
    {
        var changeSubscriptionCommand = changeSubscriptionDto.Adapt<ChangeSubscriptionCommand>();

        var subscriptionQueryHandler = _client.GetGrain<ISubscriptionQueryHandler>(UserId);

        bool.TryParse(RequestContext.Get("isFreeTrial").ToString(), out bool isFreeTrial);

        var checkIsPlanUpgradeQuery = new CheckIsPlanUpgradeQuery(isFreeTrial, changeSubscriptionCommand.CurrentPlanId, changeSubscriptionCommand.NewPlanId);
        var isUpgrade = await subscriptionQueryHandler.Receive<bool>(checkIsPlanUpgradeQuery);

        changeSubscriptionCommand = changeSubscriptionCommand with
        {
            IsDowngrade = !isUpgrade.ResultData,
            IsScheduleDowngrade = !isUpgrade.ResultData
        };

        var changeSubscriptionResponse = await _subscriptionDataService.ChangeSubscription(changeSubscriptionCommand);
        if (changeSubscriptionResponse is null || !changeSubscriptionResponse.Success) return StatusCode(StatusCodes.Status400BadRequest, changeSubscriptionResponse);

        var userCouponReader = _client.GetGrain<IUserCouponRead>(UserId);
        var (userCoupon, _) = await userCouponReader.Get();

        //this will work only for the trail plan(PendingActivation)
        if (!await userCouponReader.IsNew() && (userCoupon.GetEffectiveStatus(_dateTimeProvider).Equals(CouponStatus.PendingActivation) ||
                                                userCoupon.GetEffectiveStatus(_dateTimeProvider).Equals(CouponStatus.Active)))
        {
            //revoke coupon that are in pending state
            var userCouponWrite = _client.GetGrain<IUserCouponWrite>(UserId);
            await userCouponWrite.Receive(new RevokePendingCouponCommand());
        }

        return new JsonResult(changeSubscriptionResponse);
    }

    [HttpPost("updatepaymentcard")]
    public async Task<IActionResult> UpdatePaymentCard(UpdatePaymentCardDto updatePaymentCardDto)
    {
        var updatePaymentCardResponse = await _subscriptionDataService.UpdatePaymentCard(updatePaymentCardDto.Adapt<UpdatePaymentCardCommand>());
        if (updatePaymentCardResponse is null || !updatePaymentCardResponse.Success) return StatusCode(StatusCodes.Status400BadRequest, updatePaymentCardResponse);

        return new JsonResult(updatePaymentCardResponse);
    }

    [HttpPost("userRegistrationQuestionnaireAnswers")]
    public async Task<IActionResult> UserRegistrationQuestionnaireAnswers(UserRegistrationQuestionnaireAnswersDto userRegistrationQuestionnaireAnswersDto)
    {
        try
        {
            Log.Information("UserRegistrationQuestionnaireAnswers - Execution started for UserId: {UserId}", UserId);
            userRegistrationQuestionnaireAnswersDto.UserId = UserId;
            userRegistrationQuestionnaireAnswersDto.PartitionKey = UserId.ToString();
            userRegistrationQuestionnaireAnswersDto.RowKey = UserId.ToString();
            userRegistrationQuestionnaireAnswersDto.AgeGroup = await GetAgeGroup(userRegistrationQuestionnaireAnswersDto.BirthYear);

            await _azureTableUserService.UpsertUserRegistrationQuestionnaireAnswers(userRegistrationQuestionnaireAnswersDto);
            Log.Information("UserRegistrationQuestionnaireAnswers - Execution started for UserId: {UserId}", UserId);
            return StatusCode(StatusCodes.Status200OK);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "UserRegistrationQuestionnaireAnswers - Got exception while executing UserRegistrationQuestionnaireAnswers for UserId: {UserId} and Exception: {Ex}",
                UserId, ex);
            return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    [Route("freeldsaccount")]
    [HttpPost]
    public async Task<IActionResult> FreeLDSAccount()
    {
        try
        {
            var userWriter = _client.GetGrain<IUserWrite>(UserId);
            var outcome = await userWriter.Receive(new CreateFreeLDSAccountCommand());
            if (outcome.Failed)
                return StatusCode(StatusCodes.Status400BadRequest, outcome.Problems);
            return StatusCode(StatusCodes.Status200OK);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "freeldsaccount - Got exception while creating Free LDS account for UserId: {UserId} and Exception: {Ex}", UserId, ex);
            return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    [Route("freeldssubscription")]
    [HttpPost]
    public async Task<IActionResult> FreeLDSSubscription()
    {
        try
        {
            var userWriter = _client.GetGrain<IUserWrite>(UserId);
            var outcome = await userWriter.Receive(new AssignFreeLDSSubscriptionCommand());
            if (outcome.Failed)
                return StatusCode(StatusCodes.Status400BadRequest, outcome.Problems);
            return StatusCode(StatusCodes.Status200OK);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "freeldssubscription - Got exception while assigning Free LDS subscription for UserId: {UserId} and Exception: {Ex}", UserId, ex);
            return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    [Route("initializeOfflineFamilySearchAccess")]
    [HttpPost]
    public async Task<IActionResult> InitializeOfflineFamilySearchAccess(string accessToken, string refreshToken)
    {
        var offlineAccessTokenProvider = _client.GetGrain<IFamilySearchOfflineAccessTokenProvider>(UserId);
        await offlineAccessTokenProvider.InitializeAccessToken(accessToken, refreshToken);
        return StatusCode(StatusCodes.Status200OK);
    }

    private static Task<string> GetAgeGroup(int birthYear)
    {
        var currentYear = DateTime.Now.Year;
        var age = currentYear - birthYear;
        var ageGroup = age switch
        {
            < 35 => "Under 35",
            <= 44 => "35 - 44",
            <= 54 => "45 - 54",
            <= 64 => "55 - 64",
            <= 74 => "65 - 74",
            _ => "75 or older"
        };

        return Task.FromResult(ageGroup);
    }

    [HttpPost("userRegistrationQuestionnaireSeen")]
    public async Task UserRegistrationQuestionnaireSeen()
    {
        try
        {
            Log.Information("UserRegistrationQuestionnaireSeen - Execution started for UserId: {UserId}", UserId);
            var userBehaviourWriter = _client.GetGrain<IUserBehaviourWrite>(UserId);
            _ = await IssueCommand(userBehaviourWriter, new UserRegistrationQuestionnaireCommand());
            Log.Information("UserRegistrationQuestionnaireSeen - Execution completed for UserId: {UserId}", UserId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "UserRegistrationQuestionnaireSeen - Got exception while executing UserRegistrationQuestionnaireSeen for UserId:{UserId} and Exception:{Ex}", UserId, ex);
        }
    }

    [Route("{email}/linkaccountsbyemail")]
    [HttpPost]
    public async Task<IActionResult> LinkAccountsByEmail(string email)
    {
        try
        {
            var linkedIdentityProviders = await _authProviderService.LinkAccountsByEmail(email);
            if (linkedIdentityProviders != "Account linking failed") return StatusCode(StatusCodes.Status200OK, linkedIdentityProviders);

            return StatusCode(StatusCodes.Status400BadRequest, linkedIdentityProviders);
        }
        catch (Exception ex)
        {
            Log.Information(ex, "Error while executing LinkAccountsByEmail for email-{Email} : {Ex}", email, ex);
            return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    [HttpPost("firstHintSeen")]
    public async Task FirstHintSeen()
    {
        try
        {
            var userBehaviourWriter = _client.GetGrain<IUserBehaviourWrite>(UserId);
            _ = await IssueCommand(userBehaviourWriter, new FirstHintSeenCommand());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "FirstHintSeen - Got exception while executing FirstHintSeen for UserId: {UserId} and Exception: {Ex}", UserId, ex);
        }
    }

    [AuthorizeAdmin]
    [Route("{userId}/{auth0UserId}/UpdateAuth0Id")]
    [HttpPost]
    public async Task UpdateAuth0Id(Guid userId, string auth0UserId)
    {
        var userWriter = _client.GetGrain<IUserWrite>(userId);
        _ = await userWriter.Receive(new UpdateAuth0IdCommand(userId, auth0UserId));
    }

    [HttpPost]
    [Route("assignbookpublishingplan")]
    public async Task<IActionResult> AssignBookPublishingPlan(string token, string subscriptionLevelCouponCode = "")
    {
        var userWriter = _client.GetGrain<IUserSubscriptionWrite>(UserId);
        var outcome = await userWriter.Receive(new AssignBookPublishingPlanCommand(token, subscriptionLevelCouponCode));

        if (outcome.Failed)
        {
            return StatusCode(StatusCodes.Status400BadRequest, outcome.Problems);
        }
        else
        {
            if (await _featureManager.IsEnabledAsync(FeatureFlags.FF_SubscriptionTransactionId))
            {
                var userReader = _client.GetGrain<IUserSubscriptionRead>(UserId);
                var transactionId = await userReader.GetTransactionId();
                return Ok(new { TransactionId = transactionId });
            }
            else
            {
                return Ok();
            }
        }
    }

    private async Task<string> UpdateAuth0UserEmailByUserId(string userId, string auth0UserId, string newEmail)
    {
        string message;
        try
        {
            message = await _authProviderService.UpdateAuth0UserEmail(userId, auth0UserId, newEmail);
            if (message == "Email is already used by another user")
                message = "This email address is already associated with account.";
            else if (message != string.Empty) message = "Error occurred while updating emailid for UserId-" + userId;
        }
        catch (Exception ex)
        {
            message = "Error occurred while updating emailid for UserId-" + userId;
            _logger.LogError(ex, "{ErrorMessage}:{Ex}", message, ex);
        }

        return message;
    }

    private async Task<string> UpdateAuth0UserProfileByUserId(string auth0UserId, UserDto userDto)
    {
        var errorMessage = string.Empty;
        try
        {
            await _authProviderService.UpdateAuth0UserMetadata(auth0UserId, userDto.UserId.ToString(), userDto.GivenName.Trim(), userDto.Surname.Trim());
        }
        catch (Exception ex)
        {
            errorMessage = $"Error occurred while updating UserId {userDto.UserId}";
            _logger.LogError(ex, "{ErrorMessage}: {Ex}", errorMessage, ex);
        }

        return errorMessage;
    }

    private async Task<List<AccessDto>> DownloadAccessKeys()
    {
        var storageConnection = AppOptions.CurrentValue.BlobStorage!.Gedcom.ConnectionString;
        var containerName = AppOptions.CurrentValue.BlobStorage.Gedcom.ContainerName;

        if (CloudStorageAccount.TryParse(storageConnection, out var storageAccount))
        {
            var blobClient = storageAccount.CreateCloudBlobClient();
            var container = blobClient.GetContainerReference(containerName);
            var blob = container.GetBlockBlobReference("AccessKeys.json");

            var jsonString = await blob.DownloadTextAsync();
            var accessCodeList = JsonSerializer.Deserialize<List<AccessDto>>(jsonString);
            return accessCodeList;
        }

        return null;
    }

    private static string IsValidEmail(string emailAddress)
    {
        var errorMsg = string.Empty;
        if (string.IsNullOrWhiteSpace(emailAddress))
            return "Email address is empty";
        MailAddress mail = null;
        try
        {
            mail = new MailAddress(emailAddress);
        }
        catch (FormatException)
        {
            errorMsg = "Email format is invalid";
        }
        catch (Exception ex)
        {
            errorMsg = $"Error occurred in validating email {mail.Address}. Error {ex.Message}";
        }

        return errorMsg;
    }
}