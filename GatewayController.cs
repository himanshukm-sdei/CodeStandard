using System.Diagnostics.CodeAnalysis;
using BTIS.Contracts.GeneralLiability.Rating.RateLookup;
using CNA.V2.Domain.DTO;
using CNA.V2.Domain.Model;
using CNA.V2.Domain.ResponseModel;
using CNA.V2.Service.Helper;
using CNA.V2.Service.Utility;
using Microsoft.AspNetCore.Mvc;
using System.Net;
using CalculationCriteria = CNA.V2.Domain.Model.CalculationCriteria;
using CNA.V2.Domain.Model.CompanyPlacement;
using CNA.V2.Service.Services.Interface;
using Newtonsoft.Json.Linq;
using Consul;
using Newtonsoft.Json;
using CNA.V2.Domain.Model.Gradient;
using System.Reflection.Metadata;
using CNA.V2.Domain.Model.RiskReservation;

namespace CNA.V2.Controllers
{
    /// <summary>
    /// Gateway Controller
    /// </summary>
    [Route("Gateway")]
    [ApiController]
    public class GatewayController : ControllerBase
    {
        #region  --Inject Dependency--
        private readonly ILogger<GatewayController> _logger;
        private readonly IHelperMethod _helperMethod;
        private readonly ITokenUtility _tokenUtil;
        private readonly IService _service;
        private readonly ICnaV1Service _cnaV1Service;
        private readonly IConfiguration _configuration;
        private readonly IRatingService _ratingService;
        private readonly IGradientService _gradientService;

        /// <summary>
        /// Default Constructor
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="tokenUtility"></param>
        /// <param name="helperMethod"></param>
        /// <param name="service"></param>
        /// <param name="configuration"></param>
        /// <param name="ratingService"></param>
        /// <param name="gradientService"></param>
        /// <param name="cnaV1Service"></param>
        public GatewayController(ILogger<GatewayController> logger, ITokenUtility tokenUtility, IHelperMethod helperMethod,
            IService service, IConfiguration configuration,
            IRatingService ratingService, ICnaV1Service cnaV1Service, IGradientService gradientService)
        {
            _logger = logger;
            _tokenUtil = tokenUtility;
            _service = service;
            _helperMethod = helperMethod;
            _configuration = configuration;
            _ratingService = ratingService;
            _cnaV1Service = cnaV1Service;
            _gradientService = gradientService;
        }
        #endregion

        /// <summary>
        /// Appetite endpoint to check the Carrier is avaliable
        /// </summary>
        /// <param name="wcSubmission"></param>
        /// <returns></returns>
        [ProducesResponseType((int)HttpStatusCode.OK, Type = typeof(ResponseViewModel<bool>))]
        [HttpPost("Appetite")]
        public async Task<IActionResult> Appetite([FromBody] WCSubmissionV2 wcSubmission)
        {
            var response = new ResponseViewModel<bool>();
            try
            {
                if (EndpointAuthorization(out var authenticationStatus, out var statusCode)) return statusCode;

                // Check model is valid.
                if (!ModelState.IsValid)
                {
                    _logger.LogWarning("Bad Request : Appetite Invalid Model");
                    foreach (var e in ModelState.SelectMany(x => x.Value!.Errors))
                    {
                        response.Error?.Add(new Error() { Message = e.ErrorMessage, Description = e.ErrorMessage, Code = (int)HttpStatusCode.BadRequest });
                    }
                    response = HelperMethod.ResponseMapping<bool>((int)HttpStatusCode.BadRequest, "Bad Request", false, response.Error);
                    return BadRequest(response);
                }

                // Effective date validation
                var effectiveDateCheck = ValidateEffectiveDate(wcSubmission.ProposedEffectiveDate ?? DateTime.Now);
                if (effectiveDateCheck.Status == (int)HttpStatusCode.BadRequest)
                {
                    _logger.LogWarning("Bad Request : Invalid Effective Date");
                    response = HelperMethod.ResponseMapping<bool>((int)HttpStatusCode.BadRequest, "BadRequest", false, effectiveDateCheck.Error);
                    return BadRequest(response);
                }

                if (wcSubmission.LocationsClassifications!.Count <= 0)
                {
                    _logger.LogError($"Bad Request. LocationClassification is null in provided model");
                    response.Error?.Add(new Error
                    {
                        Code = (int)HttpStatusCode.BadRequest,
                        Description = "Please provide LocationClassification.",
                        Message = "Please provide LocationClassification."
                    });
                    response = HelperMethod.ResponseMapping<bool>((int)HttpStatusCode.BadRequest, "Bad Request. LocationClassification is not provided in request.", false, response.Error);
                    return BadRequest(response);
                }

                if (wcSubmission.LocationsClassifications!.Count > 1)
                {
                    var states = wcSubmission.LocationsClassifications.Select(lc => lc.Location.State).Distinct().ToList();
                    if (states.Count > 1)
                    {
                        _logger.LogError($"Bad Request. Multiple location classifications in different states are not supported.");
                        response.Error?.Add(new Error
                        {
                            Code = (int)HttpStatusCode.BadRequest,
                            Description = "Multiple location classifications in different states are not supported.",
                            Message = "Multiple location classifications in different states are not supported."
                        });
                        response = HelperMethod.ResponseMapping<bool>((int)HttpStatusCode.BadRequest, "Bad Request. Multiple location classifications in different states are not supported.", false, response.Error);
                        return BadRequest(response);
                    }
                }

                // Calling SplitClassCodeMapping method for mapping original class code.
                wcSubmission.LocationsClassifications = _helperMethod.MapLocationClassfication(wcSubmission.LocationsClassifications);
                string invalidClasscodeMesage = await ValidateClassCodes(wcSubmission);
                if (invalidClasscodeMesage != "")
                {
                    response.Error?.Add(new Error
                    {
                        Code = (int)HttpStatusCode.BadRequest,
                        Description = $"Appetite Failed: Class codes don't fit appetite {invalidClasscodeMesage}",
                        Message = $"Appetite Failed: Class codes don't fit appetite {invalidClasscodeMesage}"
                    });
                    response = HelperMethod.ResponseMapping<bool>((int)HttpStatusCode.BadRequest, "Bad Request", false, response.Error);
                    return BadRequest(response);
                }
                ////commented the code of Risk Reservation as its not working
                //var token = await _cnaV1Service.CreateTokenCNA1();
                //if (string.IsNullOrEmpty(token))
                //{
                //    _logger.LogError("Token Generate Service Getting Failed. LegalEntityName: {LegalEntityName}", wcSubmission.Applicant.LegalEntityName ?? wcSubmission.Applicant.DBA);

                //    // Sending True default for testing
                //    response = HelperMethod.ResponseMapping<bool>((int)HttpStatusCode.OK, "Successful", true, null);
                //    return Ok(response);
                //    //response.Error?.Add(new Error
                //    //{
                //    //    Code = (int)HttpStatusCode.OK,
                //    //    Description = $"Token Generate Service Getting Failed.",
                //    //    Message = $"Token Generate Service Getting Failed."
                //    //});
                //    //response = HelperMethod.ResponseMapping<bool>((int)HttpStatusCode.BadRequest, "Bad Request", false, response.Error);
                //    //return BadRequest(response);
                //}

                //var riskReservationResponse = await GetRiskReservation(wcSubmission, token);
                //if (riskReservationResponse?.Status != "SUCCESS")
                //{
                //    _logger.LogError("Risk reservation check failed {LegalEntityName}", wcSubmission?.Applicant?.LegalEntityName ?? wcSubmission?.Applicant.DBA);
                //    response.Error?.Add(new Error
                //    {
                //        Code = (int)HttpStatusCode.BadRequest,
                //        Description = $"Risk reservation check failed. {riskReservationResponse?.Status}",
                //        Message = $"Risk reservation check failed. {riskReservationResponse?.Status}"
                //    });
                //    response = HelperMethod.ResponseMapping<bool>((int)HttpStatusCode.BadRequest, "BadRequest : Risk reservation check failed.", false, response.Error);
                //    return BadRequest(response);
                //}

                response = HelperMethod.ResponseMapping<bool>((int)HttpStatusCode.OK, "Successful", true, null);
                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error while checking appetite: Exception:{0}. Stack Trace: {1}", ex.Message, ex.StackTrace);
                response.Error?.Add(new Error
                {
                    Code = (int)HttpStatusCode.InternalServerError,
                    Description = ex.Message == null || ex.Message == "" ? "Something went wrong" : ex.Message,
                    Message = ex.Message
                });
                response = HelperMethod.ResponseMapping<bool>((int)HttpStatusCode.InternalServerError, "Exception occurs while processing request.", false, response.Error);
                return StatusCode((int)HttpStatusCode.InternalServerError, response);
            }
        }


        /// <summary>
        /// Quote endpoint to get the rates for the carrier
        /// </summary>
        /// <param name="wcSubmissionModel"></param>
        /// <param name="schModifier"></param>
        /// <param name="quickAction"></param>
        /// <param name="noOfDays"></param>
        /// <param name="cancellationType"></param>
        /// <param name="sourceType"></param>
        /// <returns></returns>
        [ProducesResponseType((int)HttpStatusCode.OK, Type = typeof(ResponseViewModel<WCResponse>))]
        [HttpPost("Quote")]
        public async Task<IActionResult> Quote([FromBody] WCSubmissionV2 wcSubmissionModel, decimal? schModifier = null, bool quickAction = false, int? noOfDays = null, string? cancellationType = null, string? sourceType = null)
        {
            // Response object declaration.
            var response = new ResponseViewModel<WCResponse>();
            string companyPlacementCode = string.Empty;
            try
            {
                if (EndpointAuthorization(out var authenticationStatus, out var statusCode)) return statusCode;

                if (ValidateModelState(response, out var modelState)) return modelState;

                if (ValidateLocationClassification(wcSubmissionModel, response, out var result)) return result;

                // Calling SplitClassCodeMapping method for mapping original class code.
                wcSubmissionModel.LocationsClassifications = _helperMethod.MapLocationClassfication(wcSubmissionModel.LocationsClassifications);


                var primaryLocation = wcSubmissionModel?.LocationsClassifications?.FirstOrDefault(x => x.Location?.IsPrimary == true) ??
                                                                            wcSubmissionModel?.LocationsClassifications?.FirstOrDefault();

                if (wcSubmissionModel!.KeyValues != null)
                {
                    companyPlacementCode = wcSubmissionModel.KeyValues.Where(x => x.Key == "CompanyCode").Select(y => y.Value).FirstOrDefault() ?? string.Empty;
                }
                if (string.IsNullOrEmpty(companyPlacementCode))
                {
                    var company = await _service.GetDefaultCompany(primaryLocation.Location.State);
                    companyPlacementCode = company?.Code;

                }

                string invalidClasscodeMesage = await ValidateClassandCompanyCodes(wcSubmissionModel, companyPlacementCode);
                if (ValidateInValidClasCode(invalidClasscodeMesage, response, out var actionResult)) return actionResult;
                var config_look = await _service.GetConfigurationByState(primaryLocation!.Location!.State, wcSubmissionModel!.ProposedEffectiveDate);
                if (ValidateConfigLookUp(wcSubmissionModel, config_look, primaryLocation, response, out var configResult)) return configResult;

                // if (ValidateRiskAndCompanyCode(wcSubmissionModel, response, out var companyPlacementCode, out var riskCompanyResult)) return riskCompanyResult;

                _logger.LogInformation("Calling quote api with request :{requestBody}", JsonConvert.SerializeObject(wcSubmissionModel));


                // get minimum premium
                decimal? minPremium = await _helperMethod.GetMinimumPremiumForClass(wcSubmissionModel, noOfDays, companyPlacementCode);

                //get expense/prorateexpense constant
                decimal? expenseConstant = await _helperMethod.CalculateProRateExpenseConstant(wcSubmissionModel, config_look, noOfDays);

                //Get Short rate penality
                var shortRatePenalty = new ShortRatePenalties();
                if (!string.IsNullOrEmpty(cancellationType) && (cancellationType == "01" || cancellationType == "02"))
                {
                    _logger.LogDebug($"ProRate Payroll in case of Canellation from Rate POST endpoint. SubmissionId {wcSubmissionModel?.SubmissionId?.ToString()}");
                    if (string.IsNullOrEmpty(sourceType))
                    {
                        wcSubmissionModel.LocationsClassifications = _helperMethod.ProRatePayroll(wcSubmissionModel, noOfDays.Value);
                    }
                    //calculate ShortRatePenalty
                    if (cancellationType == "02")
                    {
                        _logger.LogDebug($"Calculate ShortRatePenalty from Rate POST endpoint. SubmissionId {wcSubmissionModel?.SubmissionId?.ToString()}");
                        shortRatePenalty = await _service.GetShortRatePenalties(noOfDays);
                    }
                }

                var classificationPayroll = primaryLocation?.Classifications.GroupBy(i => i.ClassCode).ToDictionary(i => i.Key, i => i.Sum(item => item.Payroll) ?? 0);

                //calculate Specific waiver 
                decimal? specificWaiver = 0;
                if (wcSubmissionModel?.Applicant?.BlanketWaiverSubrogation != true && wcSubmissionModel?.SWLocationsClassifications != null &&
                    wcSubmissionModel?.SWLocationsClassifications?.Count > 0 && primaryLocation?.Location?.State?.ToString() != "AZ")
                {
                    _logger.LogInformation($"Calculate SpecificWaiver from Rate POST endpoint. SubmissionId {wcSubmissionModel?.SubmissionId?.ToString()}");
                    specificWaiver = await CalculateSpecificWaiver(wcSubmissionModel, companyPlacementCode);
                }

                var objCalculationRequest = RatingCriteriaRequest(wcSubmissionModel, schModifier, sourceType, primaryLocation, classificationPayroll, specificWaiver,
                                                                    config_look, minPremium, cancellationType, shortRatePenalty);

                try
                {
                    response = await _ratingService.GetRatingResponse(wcSubmissionModel, primaryLocation, config_look, companyPlacementCode, minPremium, expenseConstant, objCalculationRequest, schModifier, quickAction, noOfDays);
                    return Ok(response);
                }
                catch (Exception ex)
                {
                    _logger.LogError("Error from rating details.: Exception:{0}. Stack Trace: {1}", ex.Message, ex.StackTrace);
                    response.Error?.Add(new Error
                    {
                        Code = (int)HttpStatusCode.BadRequest,
                        Description = "Error from Rating details endpoint.",
                        Message = "Error from Rating details endpoint."
                    });

                    response.Response = new WCResponse { success = false, quote = null, SubmissionStatus = "Error", submissionid = objCalculationRequest.SubmissionId };
                    return BadRequest(response);
                }
            }
            catch (Exception ex)
            {
                // If there is an exception while finding carriers.
                _logger.LogError("Error while mapping request: Exception:{0}. Stack Trace: {1}", ex.Message, ex.StackTrace);
                response.Error?.Add(new Error
                {
                    Code = (int)HttpStatusCode.InternalServerError,
                    Description = ex.Message == null || ex.Message == "" ? "Something went wrong" : ex.Message,
                    Message = ex.Message
                });
                response = HelperMethod.ResponseMapping<WCResponse>((int)HttpStatusCode.InternalServerError, "Exception occurs while processing request.", null, response.Error);
                response.Response = new WCResponse { success = false, quote = null, SubmissionStatus = "Error" };
                return StatusCode((int)HttpStatusCode.InternalServerError, response);
            }
        }

        /// <summary>
        /// This Endpoint is use to get rates by submissionId
        /// </summary>
        /// <param name="wcSubmissionModel"></param>
        /// <param name="ratingSubmissionId"></param>
        /// <param name="schModifier"></param>
        /// <param name="quickAction"></param>
        /// <param name="noOfDays"></param>
        /// <param name="sourceType"></param>
        /// /// <param name="cancellationType"></param>
        /// <returns></returns>
        [ProducesResponseType((int)HttpStatusCode.OK, Type = typeof(ResponseViewModel<WCResponse>))]
        [HttpPut("Quote/{ratingSubmissionId}")]
        public async Task<IActionResult> UpdateQuote([FromBody] WCSubmissionV2 wcSubmissionModel, string ratingSubmissionId, decimal? schModifier = null, bool quickAction = false, int? noOfDays = null, string? cancellationType = null, string? sourceType = null)
        {
            var response = new ResponseViewModel<WCResponse>();
            string companyPlacementCode = string.Empty;
            try
            {
                if (EndpointAuthorization(out var authenticationStatus, out var statusCode)) return statusCode;

                if (ValidateModelState(response, out var modelState)) return modelState;

                if (string.IsNullOrEmpty(ratingSubmissionId))
                {
                    _logger.LogWarning("Submission id is empty or null.");
                    response.Error?.Add(new Error
                    {
                        Code = (int)HttpStatusCode.BadRequest,
                        Description = "Submission id can not be empty or null.",
                        Message = "Rate Failed"
                    });
                    response = HelperMethod.ResponseMapping<WCResponse>((int)HttpStatusCode.BadRequest, "BadRequest", null, response.Error);
                    return BadRequest(response);
                }

                // Calling SplitClassCodeMapping method for mapping original class code.
                wcSubmissionModel.LocationsClassifications = _helperMethod.MapLocationClassfication(wcSubmissionModel.LocationsClassifications);

                if (ValidateLocationClassification(wcSubmissionModel, response, out var result)) return result;


                var primaryLocation = wcSubmissionModel?.LocationsClassifications?.FirstOrDefault(x => x.Location?.IsPrimary == true)
                    ?? wcSubmissionModel?.LocationsClassifications?.FirstOrDefault();

                if (wcSubmissionModel!.KeyValues != null)
                {
                    companyPlacementCode = wcSubmissionModel.KeyValues.Where(x => x.Key == "CompanyCode").Select(y => y.Value).FirstOrDefault() ?? string.Empty;
                }
                if (string.IsNullOrEmpty(companyPlacementCode))
                {
                    var company = await _service.GetDefaultCompany(primaryLocation.Location.State);
                    companyPlacementCode = company?.Code;

                }

                string invalidClasscodeMesage = await ValidateClassandCompanyCodes(wcSubmissionModel, companyPlacementCode);
                if (ValidateInValidClasCode(invalidClasscodeMesage, response, out var actionResult)) return actionResult;

                var config_look = await _service.GetConfigurationByState(primaryLocation!.Location!.State, wcSubmissionModel!.ProposedEffectiveDate);
                if (ValidateConfigLookUp(wcSubmissionModel, config_look, primaryLocation, response, out var configResult)) return configResult;

                //if (ValidateRiskAndCompanyCode(wcSubmissionModel, response, out var companyPlacementCode, out var riskCompanyResult)) return riskCompanyResult;

                _logger.LogInformation("Calling update quote api with request :{requestBody}", JsonConvert.SerializeObject(wcSubmissionModel));
                // get minimum premium
                decimal? minPremium = await _helperMethod.GetMinimumPremiumForClass(wcSubmissionModel, noOfDays, companyPlacementCode);

                //get expense/prorateexpense constant
                decimal? expenseConstant = await _helperMethod.CalculateProRateExpenseConstant(wcSubmissionModel, config_look, noOfDays);

                var classificationPayroll = primaryLocation?.Classifications.GroupBy(i => i.ClassCode).ToDictionary(i => i.Key, i => i.Sum(item => item.Payroll) ?? 0);

                var shortRatePenalty = new ShortRatePenalties();
                // calculate prorated payroll
                if (!string.IsNullOrEmpty(cancellationType) && (cancellationType == "01" || cancellationType == "02"))
                {
                    _logger.LogDebug($"ProRate Payroll in case of Canellation from Rate PUT endpoint. SubmissionId {wcSubmissionModel?.SubmissionId?.ToString()}");
                    if (string.IsNullOrEmpty(sourceType))
                    {
                        wcSubmissionModel.LocationsClassifications = _helperMethod.ProRatePayroll(wcSubmissionModel, noOfDays.Value);
                    }
                    //calculate ShortRatePenalty
                    if (cancellationType == "02")
                    {
                        _logger.LogDebug($"Calculate ShortRatePenalty from Rate PUT endpoint: SubmissionId{wcSubmissionModel?.SubmissionId?.ToString()}");
                        shortRatePenalty = await _service.GetShortRatePenalties(noOfDays);
                    }
                }

                //calculate Specific waiver
                decimal? specificWaiver = 0;
                if (wcSubmissionModel?.Applicant?.BlanketWaiverSubrogation != true && wcSubmissionModel?.SWLocationsClassifications != null &&
                    wcSubmissionModel?.SWLocationsClassifications?.Count > 0)
                {
                    _logger.LogInformation($"Calculate SpecificWaiver from Rate POST endpoint. SubmissionId {wcSubmissionModel?.SubmissionId?.ToString()}");
                    specificWaiver = await CalculateSpecificWaiver(wcSubmissionModel, companyPlacementCode);
                }
                var objCalculationRequest = RatingCriteriaRequest(wcSubmissionModel, schModifier, sourceType, primaryLocation, classificationPayroll, specificWaiver,
                                                                   config_look, minPremium, cancellationType, shortRatePenalty);
                objCalculationRequest.SubmissionId = ratingSubmissionId;

                try
                {
                    response = await _ratingService.GetRatingResponse(wcSubmissionModel, primaryLocation, config_look, companyPlacementCode, minPremium, expenseConstant, objCalculationRequest, schModifier, quickAction, noOfDays);
                    return Ok(response);
                }
                catch (Exception ex)
                {
                    _logger.LogError("Error from rating details.: Exception:{0}. Stack Trace: {1}", ex.Message, ex.StackTrace);
                    response.Error?.Add(new Error
                    {
                        //Code = (int)originalRatingResponse.StatusCode,
                        //Description = originalRatingResponseString,
                        Message = "Error from Rating details endpoint."
                    });
                    //response = HelperMethod.ResponseMapping<WCResponse>((int)originalRatingResponse.StatusCode, "Error from Rating details endpoint.", null, response.Error);
                    response.Response = new WCResponse { success = false, quote = null, SubmissionStatus = "Error", submissionid = objCalculationRequest.SubmissionId };
                    return BadRequest(response);
                }
            }
            catch (Exception ex)
            {
                // If there is an exception while finding carriers.
                _logger.LogError("Error while mapping request: Exception:{0}. Stack Trace: {1}", ex.Message, ex.StackTrace);
                response.Error?.Add(new Error
                {
                    Code = (int)HttpStatusCode.InternalServerError,
                    Description = ex.Message == null || ex.Message == "" ? "Something went wrong" : ex.Message,
                    Message = ex.Message
                });
                response = HelperMethod.ResponseMapping<WCResponse>((int)HttpStatusCode.InternalServerError, "Exception occurs while processing request.", null, response.Error);
                response.Response = new WCResponse { success = false, quote = null, SubmissionStatus = "Error" };
                return StatusCode((int)HttpStatusCode.InternalServerError, response);
            }
        }

        /// <summary>
        /// This method is being to get company placement code api response
        /// </summary>
        /// <param name="submissionId"></param>
        /// <returns></returns>
        [ProducesResponseType((int)HttpStatusCode.OK, Type = typeof(ResponseViewModel<string>))]
        [HttpGet("CompanyPlacementCode/{submissionId}")]
        public async Task<IActionResult> CompanyPlacementCode(string submissionId)
        {
            var response = new ResponseViewModel<string>();
            try
            {
                if (EndpointAuthorization(out var authenticationStatus, out var statusCode)) return statusCode;

                if (string.IsNullOrEmpty(submissionId))
                {
                    _logger.LogWarning($"Bad request. Passed submisionID: {submissionId}");
                    response.Error?.Add(new Error
                    {
                        Code = (int)HttpStatusCode.BadRequest,
                        Description = "Invalid submissionID passed.",
                        Message = "Bad request"
                    });
                    response = HelperMethod.ResponseMapping<string>((int)HttpStatusCode.BadRequest, "Bad request.", null, response.Error);
                    return BadRequest(response);
                }

                // Calling GetBySubmissionID repository method.
                _logger.LogInformation($"Calling GetBySubmissionID repository method with input submissionID = {submissionId}");
                var wcSubmissionModel = await _service.GetWcSubmission(submissionId);
                if (wcSubmissionModel == null)
                {
                    _logger.LogWarning($"Submission details not found from GetBySubmissionID method for submissionID = {submissionId}");
                    response.Error?.Add(new Error
                    {
                        Code = (int)HttpStatusCode.NotFound,
                        Description = "Submission not found.",
                        Message = "Please provide correct submissionID."
                    });
                    response = HelperMethod.ResponseMapping<string>((int)HttpStatusCode.NotFound, "Not found.", null, response.Error);
                    return NotFound(response);
                }
                // Calling SplitClassCodeMapping method for mapping original class code.
                wcSubmissionModel.Submission.LocationsClassifications = _helperMethod.MapLocationClassfication(wcSubmissionModel.Submission.LocationsClassifications);

                //var companyReplacementResponse = await _service.CompanyPlacement(_helperMethod.MapCompanyReplacement(wcSubmissionModel.Submission));

                List<PriorCarrierPolicyInformation> priorCarrierPolicyInformation = new();
                List<WorkersCompRatingState> workersCompRatingState = new();


                var governingClassifications = _helperMethod.GetGoverningLocation(wcSubmissionModel.Submission, workersCompRatingState);

                var governingClassCode = governingClassifications.OrderByDescending(m => m.Payroll).FirstOrDefault()?.ClassCode;
                var governingStateCode = governingClassifications.OrderByDescending(m => m.Payroll).FirstOrDefault()?.StateCode;

                var company = await _service.GetDefaultCompany(governingStateCode);
                string? companyPlacementCode = company?.Code;

                var classCodeList = await _service.GetSubClassCode(governingStateCode, governingClassCode, wcSubmissionModel.Submission.ProposedEffectiveDate.Value);
                var classCode = classCodeList?.OrderByDescending(x => x.StartDate).FirstOrDefault(x => x.DefaultIndicator == 1) ?? classCodeList?.FirstOrDefault();
                if (classCode == null)
                {
                    _logger.LogInformation("SIC Mapping not found for SubmissionID: {submissionID} GoverningStateCode: {governingStateCode} GoverningClassCode: {governingClassCode} ProposedEffectiveDate: {wcSubmissionModel?.Submission.ProposedEffectiveDate}", submissionId, governingStateCode, governingClassCode, wcSubmissionModel?.Submission.ProposedEffectiveDate);
                    response = HelperMethod.ResponseMapping<string>((int)HttpStatusCode.OK, "Successful", companyPlacementCode, null);
                    return Ok(response);
                }
                var token = await _cnaV1Service.CreateTokenCNA1();
                if (string.IsNullOrEmpty(token))
                {
                    _logger.LogError("Token Generate Service Getting Failed. SubmissionId: {submissionId}", submissionId);
                    //response.Error?.Add(new Error
                    //{
                    //    Code = (int)HttpStatusCode.BadRequest,
                    //    Description = "Token Generate Service Getting Failed.",
                    //    Message = "Token Generate Service Getting Failed."
                    //});
                    //response = HelperMethod.ResponseMapping<string>((int)HttpStatusCode.BadRequest, "Bad Request", null, response.Error);
                    //return BadRequest(response);

                    //Sending NF default for testing if error occured
                    response = HelperMethod.ResponseMapping<string>((int)HttpStatusCode.OK, "Successful", companyPlacementCode, null);
                    return Ok(response);
                }

                foreach (var insuranceHistory in wcSubmissionModel.Submission.InsuranceHistory)
                {
                    if (insuranceHistory != null)
                    {
                        var PriorCarrierPolicyInformation = new PriorCarrierPolicyInformation()
                        {
                            PolicyEffectiveDate = insuranceHistory.StartDate.ToString("yyyy-MM-dd"),
                            PolicyExpirationDate = insuranceHistory.EndDate.ToString("yyyy-MM-dd"),
                            PolicyNumber = insuranceHistory.PolicyNumber,
                            NumberofTotalClaims = insuranceHistory.NoOfClaims?.ToString(),
                        };
                    }
                }
                var companyPlacementRequest = new CompanyPlacementRequest
                {
                    writtenDate = DateTime.Now.ToString("yyyy-MM-dd"),
                    policyEffectiveDate = wcSubmissionModel.Submission.ProposedEffectiveDate.Value.ToString("yyyy-MM-dd"),
                    policyExpirationDate = !string.IsNullOrEmpty(wcSubmissionModel.Submission.ProposedExpirationDate?.ToString()) ? wcSubmissionModel.Submission.ProposedExpirationDate?.ToString("yyyy-MM-dd") : wcSubmissionModel.Submission.ProposedEffectiveDate.Value.AddYears(1).ToString("yyyy-MM-dd"),
                    sicCode = classCode?.SIC,
                    primaryRiskState = governingStateCode,
                    priorCarrierPolicyInformations = priorCarrierPolicyInformation,
                    workersCompRatingState = workersCompRatingState,
                    quoteNumber=submissionId.ToUpper().Replace("QMWC", "")


                };
                var companyPlacementResponse = await _cnaV1Service.GetCompanyPlacement(companyPlacementRequest, token, submissionId);
                if (companyPlacementResponse != null && companyPlacementResponse.status?.ToUpper() == "SUCCESS")
                {
                    var currentCompany = companyPlacementResponse.carrierEvaluationResultJson?.Where(c => c.isRecommended == "true").FirstOrDefault();
                    if (currentCompany != null)
                    {
                        companyPlacementCode = currentCompany.companyAbbreviation;
                        _logger.LogInformation("CNA company placement recommended code SubmissionId: {submissionId} CompanyPlacementCode: {companyPlacementCode} ", submissionId, companyPlacementCode);
                    }
                }
                //    if (companyReplacementResponse?.Body?.CNAMessageResponse?.Message?.COMMAND?.CalculateRiskEvaluationResponse?.AssessmentActivity?.ActivityStatus != "SUCCESS")
                //{
                //    _logger.LogError("Company replacement check failed {SubmissionId}", wcSubmissionModel?.SubmissionId);
                //    response.Error?.Add(new Error
                //    {
                //        Code = (int)HttpStatusCode.BadRequest,
                //        Description = "Company replacement check failed.",
                //        Message = "Company replacement check failed."
                //    });
                //    response = HelperMethod.ResponseMapping<string>((int)HttpStatusCode.BadRequest, "BadRequest : Company replacement failed.", null, response.Error);
                //    return BadRequest(response);
                //}
                //if (companyReplacementResponse.Body?.CNAMessageResponse?.Message?.COMMAND?.CalculateRiskEvaluationResponse?.AssessmentActivity?.EvaluationResult?.ActionRecommendation == "ACCEPT")
                //{
                //    var carrierEvaluationList = companyReplacementResponse.Body?.CNAMessageResponse?.Message?.
                //         COMMAND?.CalculateRiskEvaluationResponse?.AssessmentActivity?.EvaluationResult?.
                //         CarrierEvaluationResult.Where(c => c.ActionRecommendation == "ACCEPT" && c.IsRecommended == true).ToList().OrderBy(c => c.CarrierDeviation).FirstOrDefault();
                //    companyPlacementCode = carrierEvaluationList?.CompanyAbbreviation ?? "NF";
                //}
                response = HelperMethod.ResponseMapping<string>((int)HttpStatusCode.OK, "Successful", companyPlacementCode, null);
                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error while obtaining the companyPlacementCode : Exception: {Message}. Stack Trace: {StackTrace}", ex.Message, ex.StackTrace);
                response.Error?.Add(new Error
                {
                    Code = (int)HttpStatusCode.InternalServerError,
                    Description = ex.Message == null || ex.Message == "" ? "Something went wrong" : ex.Message,
                    Message = ex.Message ?? "Something went wrong",
                    More_Info = ex.StackTrace ?? ""
                });
                response = HelperMethod.ResponseMapping<string>((int)HttpStatusCode.InternalServerError, "Exception occurs while processing request.", null, response.Error);
                return StatusCode((int)HttpStatusCode.InternalServerError, response);
            }
        }

        /// <summary>
        /// This method is being to get risk reservation for given submission
        /// </summary>
        /// <param name="submissionId"></param>
        [ProducesResponseType((int)HttpStatusCode.OK, Type = typeof(ResponseViewModel<bool>))]
        [HttpGet("RiskReservation/{submissionId}")]
        public async Task<IActionResult> RiskReservation(string submissionId)
        {
            var response = new ResponseViewModel<bool>();
            try
            {
                if (EndpointAuthorization(out var authenticationStatus, out var statusCode)) return statusCode;
                if (string.IsNullOrEmpty(submissionId))
                {
                    _logger.LogWarning($"Bad request. Passed submisionID: {submissionId}");
                    response.Error?.Add(new Error
                    {
                        Code = (int)HttpStatusCode.BadRequest,
                        Description = "Invalid submissionID passed.",
                        Message = "Bad request"
                    });
                    response = HelperMethod.ResponseMapping<bool>((int)HttpStatusCode.BadRequest, "Bad request.", false, response.Error);
                    return BadRequest(response);
                }
                var token = await _cnaV1Service.CreateTokenCNA1();
                if (string.IsNullOrEmpty(token))
                {
                    _logger.LogError("Token Generate Service Getting Failed. SubmissionId: {submissionId}", submissionId);

                    //Sending True default for testing
                    response = HelperMethod.ResponseMapping<bool>((int)HttpStatusCode.OK, "Successful", true, null);
                    return Ok(response);
                    //response.Error?.Add(new Error
                    //{
                    //    Code = (int)HttpStatusCode.OK,
                    //    Description = $"Token Generate Service Getting Failed.",
                    //    Message = $"Token Generate Service Getting Failed."
                    //});
                    //response = HelperMethod.ResponseMapping<bool>((int)HttpStatusCode.BadRequest, "Bad Request", false, response.Error);
                    //return BadRequest(response);
                }
                // Calling GetBySubmissionID repository method.
                _logger.LogInformation($"Calling GetBySubmissionID repository method with input submissionID = {submissionId}");
                var wcSubmissionModel = await _service.GetWcSubmission(submissionId);
                if (wcSubmissionModel == null)
                {
                    _logger.LogWarning($"Submission details not found from GetBySubmissionID method for submissionID = {submissionId}");
                    response.Error?.Add(new Error
                    {
                        Code = (int)HttpStatusCode.NotFound,
                        Description = "Submission not found.",
                        Message = "Please provide correct submissionID."
                    });
                    response = HelperMethod.ResponseMapping<bool>((int)HttpStatusCode.NotFound, "Not found.", false, response.Error);
                    return NotFound(response);
                }

                // Calling SplitClassCodeMapping method for mapping original class code.
                wcSubmissionModel.Submission.LocationsClassifications = _helperMethod.MapLocationClassfication(wcSubmissionModel.Submission.LocationsClassifications);


                var riskReservationResponse = await GetRiskReservation(wcSubmissionModel.Submission, token);
                //Sending True if risk reservation failed for testing
                if (riskReservationResponse?.Status != "SUCCESS")
                {
                    _logger.LogError("Risk reservation check failed {SubmissionId}", wcSubmissionModel?.SubmissionId);
                    response.Error?.Add(new Error
                    {
                        Code = (int)HttpStatusCode.BadRequest,
                        Description = $"Risk reservation check failed.{riskReservationResponse?.Status}",
                        Message = $"Risk reservation check failed.{riskReservationResponse?.Status}"
                    });
                    response = HelperMethod.ResponseMapping<bool>((int)HttpStatusCode.BadRequest, "BadRequest : Risk reservation check failed.", false, response.Error);
                    return BadRequest(response);
                }
                response = HelperMethod.ResponseMapping<bool>((int)HttpStatusCode.OK, "Successful", true, null);
                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error while mapping request: Exception:{0}. Stack Trace: {1}", ex.Message, ex.StackTrace);
                response.Error?.Add(new Error
                {
                    Code = (int)HttpStatusCode.InternalServerError,
                    Description = string.IsNullOrEmpty(ex.Message) ? "Something went wrong" : ex.Message,
                    Message = string.IsNullOrEmpty(ex.Message) ? "Internal Server Error" : ex.Message
                });
                response = HelperMethod.ResponseMapping<bool>((int)HttpStatusCode.InternalServerError, "Exception occurs while processing request.", false, response.Error);
                return StatusCode((int)HttpStatusCode.InternalServerError, response);
            }
        }

        /// <summary>
        /// This Endpoint is use to get rating details
        /// </summary>
        /// <param name="criteria">model</param>
        /// <param name="expenseConstant">ExpenseConstant
        /// <param name="companyPlacementCode">ExpenseConstant</param>
        /// <returns>CalculationData</returns>
        [ProducesResponseType((int)HttpStatusCode.OK, Type = typeof(ResponseViewModel<WC_ratings>))]
        [HttpPost("RatingDetails")]
        public async Task<IActionResult> RatingDetails([FromBody] RatingCriteria criteria, decimal? expenseConstant, string companyPlacementCode)
        {
            var response = new ResponseViewModel<WC_ratings>();
            try
            {
                _logger.LogInformation("Calling EndpointAuthorization method to validate JWT Token.");
                var authenticationStatus = _tokenUtil.EndpointAuthorization(Request);

                if (authenticationStatus.Status != (int)HttpStatusCode.OK)
                {
                    _logger.LogError("Request token is invalid");
                    return StatusCode(authenticationStatus.Status, authenticationStatus);
                }
                if (!ModelState.IsValid)
                {
                    _logger.LogWarning("Bad Request : RatingDetails POST Invalid Model");
                    response.Error.AddRange(from e in ModelState.SelectMany(x => x.Value.Errors)
                                            select new Error() { Message = e.ErrorMessage, Description = e.ErrorMessage, Code = (int)HttpStatusCode.BadRequest });
                    response = HelperMethod.ResponseMapping<WC_ratings>((int)HttpStatusCode.BadRequest, "Bad Request", null, response.Error);
                    return BadRequest(response);
                }
                var config_look = await _service.GetConfigurationByState(criteria.State, criteria?.EffectiveDate);
                var ratingWorksheet = await _ratingService.GetRatingDetails(criteria, expenseConstant, companyPlacementCode, config_look);
                var wcRating = await _ratingService.SaveRatingSheet(criteria, companyPlacementCode, ratingWorksheet);
                response = HelperMethod.ResponseMapping((int)HttpStatusCode.OK, "Success.", wcRating, null);
                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error while Get submissions Report: Exception: {ex.Message}. Stack Trace: {ex.StackTrace}");
                response.Error?.Add(new Error
                {
                    Code = (int)HttpStatusCode.InternalServerError,
                    Description = ex.Message == null || ex.Message == "" ? "Something went wrong" : ex.Message,
                    Message = ex.Message,
                    More_Info = ex.StackTrace
                });
                response = HelperMethod.ResponseMapping<WC_ratings>((int)HttpStatusCode.InternalServerError, "Exception occurs while processing request.", null, response.Error);
                return StatusCode((int)HttpStatusCode.InternalServerError, response);
            }
        }

        /// <summary>
        /// This method is being to get the list of subclasscodes for a given state and classcode.
        /// </summary>
        /// <remarks>This method is used to get subclasscodes for passed state and classcode.</remarks>
        /// <param name="state">State</param>
        /// <param name="classcode">Classcode</param>
        /// <param name="effectiveDateYYYYMMDD">Effective Date</param>
        /// <returns>Success response along with subclasscodes details</returns>
        /// <response code="200">List of classcodes and subclasscodes.</response>
        /// <response code="400">Bad Request</response>
        /// <response code="404">Not Found</response>
        /// <response code="500">Exception message.</response>
        [HttpGet("Classcodes/{state}/{classcode}/Subclasscodes")]
        [ProducesResponseType((int)HttpStatusCode.OK, Type = typeof(ResponseViewModel<SubclassCodes>))]
        public async Task<IActionResult> SubClassCode(string state, string classcode, DateTime? effectiveDateYYYYMMDD)
        {
            var response = new ResponseViewModel<SubclassCodes>();
            try
            {
                if (EndpointAuthorization(out var authenticationStatus, out var statusCode)) return statusCode;

                if (string.IsNullOrEmpty(state) || string.IsNullOrEmpty(classcode))
                {
                    // If error in model state.
                    _logger.LogError("Bad request. State or classcode is empty or invalid");
                    response.Error?.Add(new Error
                    {
                        Code = (int)HttpStatusCode.BadRequest,
                        Description = string.Format("State or classcode is empty or invalid"),
                        Message = "Bad request"
                    });
                    response = HelperMethod.ResponseMapping<SubclassCodes>((int)HttpStatusCode.BadRequest, "Bad Request", null, response.Error);
                    return BadRequest(response);
                }


                effectiveDateYYYYMMDD = effectiveDateYYYYMMDD == null ? DateTime.Today : effectiveDateYYYYMMDD;
                var actualClasscode = _helperMethod.SplitClassCodeMapping(state, classcode);

                _logger.LogInformation($"Calling SubClassCode service method with state={state}, classcode={classcode} and effectiveDate={effectiveDateYYYYMMDD}");
                var subclasscode = await _service.GetSubClassCode(state, actualClasscode, Convert.ToDateTime(effectiveDateYYYYMMDD));



                if (!subclasscode?.Any() == true)
                {
                    _logger.LogError($"Classcode not found for classcode:{classcode} and state:{state}");
                    response = HelperMethod.ResponseMapping<SubclassCodes>((int)HttpStatusCode.NotFound, $"Classcode not found for classcode:{classcode} and state:{state}", null, null);
                    return NotFound(response);
                }

                var defaultSiCode = subclasscode?.Where(x => x.DefaultIndicator == 1)?.FirstOrDefault()?.SICSequence ?? subclasscode?.FirstOrDefault()?.SICSequence;


                List<SubclassCode> subclasses = new();
                for (int i = 0; i < subclasscode?.Count; i++)
                {
                    subclasses.Add(new SubclassCode()
                    {
                        Subcode = subclasscode?[i]?.SICSequence,
                        Desc = subclasscode?[i]?.SubClassCodeDescription
                    });
                }
                SubclassCodes subclassCodes = new() { Classcode = classcode, State = state, Selected = null, DefaultSic = defaultSiCode, Subcodes = subclasses };
                response = HelperMethod.ResponseMapping((int)HttpStatusCode.OK, "Successful", subclassCodes, null);
                return Ok(response);
            }
            catch (Exception ex)
            {
                // If there is an exception while calling service.
                _logger.LogError($"Error while getting classcode and subclasscode code details: Exception: {ex.Message}. Stack Trace: {ex.StackTrace}");
                response.Error?.Add(new Error
                {
                    Code = (int)HttpStatusCode.InternalServerError,
                    Description = ex.Message == null || ex.Message == "" ? "Something went wrong" : ex.Message,
                    Message = ex.Message
                });
                response = HelperMethod.ResponseMapping<SubclassCodes>((int)HttpStatusCode.InternalServerError, "Exception occurs while processing request.", null, response.Error);
                return StatusCode((int)HttpStatusCode.InternalServerError, response);
            }
        }

        /// <summary>
        /// This Endpoint is use to get Gradient rates for a QMWC submission
        /// </summary>
        /// <param name="submissionId"></param>
        /// <param name="model">Request body</param>
        /// <param name="policyNumber">Request body</param>
        /// <returns></returns>
        [ProducesResponseType((int)HttpStatusCode.OK, Type = typeof(ResponseViewModel<GradientAIResponse>))]
        [HttpPost("GradientRate/{submissionId}")]
        public async Task<IActionResult> GradientRate([FromBody] WCSubmissionV2 model, string submissionId, string? policyNumber)
        {
            // Response object declaration.
            var response = new ResponseViewModel<GradientAIResponse>();
            try
            {
                _logger.LogDebug("Calling EndpointAuthorization method to validate JWT Token.");
                var authenticationStatus = _tokenUtil.EndpointAuthorization(Request);
                if (authenticationStatus.Status != (int)HttpStatusCode.OK)
                {
                    _logger.LogError("Request token is invalid");
                    return StatusCode(authenticationStatus.Status, authenticationStatus);
                }
                // Checking model state.
                if (!ModelState.IsValid)
                {
                    _logger.LogWarning("Bad Request : GradientRate POST endpoinnt Invalid Model");
                    response.Error?.Add(new Error
                    {
                        Code = (int)HttpStatusCode.BadRequest,
                        Description = "Invalid WCSubmission model is passed.",
                        Message = "Bad Request"
                    });
                    response = HelperMethod.ResponseMapping<GradientAIResponse>((int)HttpStatusCode.BadRequest, "Bad Request", null, response.Error);
                    return BadRequest(response);
                }

                if (string.IsNullOrEmpty(submissionId))
                {
                    _logger.LogWarning("Submission id is empty or null.");
                    response.Error?.Add(new Error
                    {
                        Code = (int)HttpStatusCode.BadRequest,
                        Description = "Submission id can not be empty or null.",
                        Message = "Gradient Rate Failed"
                    });
                    response = HelperMethod.ResponseMapping<GradientAIResponse>((int)HttpStatusCode.BadRequest, "BadRequest", null, response.Error);
                    return BadRequest(response);
                }

                //Effective date validation
                var effectiveDateCheck = ValidateEffectiveDate(model.ProposedEffectiveDate.Value);
                if (effectiveDateCheck.Status == (int)HttpStatusCode.BadRequest)
                {
                    response = HelperMethod.ResponseMapping<GradientAIResponse>((int)HttpStatusCode.BadRequest, "BadRequest", null, effectiveDateCheck.Error);
                    return BadRequest(response);
                }

                // Calling SplitClassCodeMapping method for mapping original class code.
                model.LocationsClassifications = _helperMethod.MapLocationClassfication(model.LocationsClassifications);

                _logger.LogInformation("Mapping GradientRateAI model with WCSubmission model. SubmissionId: {submissionId}, PolicyNumber: {policyNumber}", submissionId, policyNumber);
                var gradientAIRequest = await MapGradientAIData(model, submissionId, policyNumber);
                _logger.LogInformation("Calling GradientRateAI service. SubmissionId: {submissionId}, PolicyNumber: {policyNumber}", submissionId, policyNumber);
                var gradientAIResponse = await _gradientService.PostGradient(gradientAIRequest);
                var responseString = await gradientAIResponse.Content.ReadAsStringAsync();
                var gradientResponse = new GradientAIResponse();
                if (gradientAIResponse.StatusCode == HttpStatusCode.OK)
                {
                    gradientResponse = JsonConvert.DeserializeObject<GradientAIResponse>(responseString);
                    response = HelperMethod.ResponseMapping<GradientAIResponse>((int)HttpStatusCode.OK, "Successful", gradientResponse, null);
                    return Ok(response);
                }
                else
                {

                    _logger.LogError("Bad Request: Status:{0}. Response: {1}", gradientAIResponse.StatusCode, responseString);
                    gradientResponse = new GradientAIResponse { StatusCode = (int)gradientAIResponse.StatusCode, Validation = new GradientValidation { Status = "Error", Error = responseString }, Message = responseString, HttpStatusCode = (int)gradientAIResponse.StatusCode };
                    response = HelperMethod.ResponseMapping<GradientAIResponse>((int)HttpStatusCode.BadRequest, $"{gradientAIResponse.StatusCode} response from GradientAI", gradientResponse, null);
                    return BadRequest(response);
                }
            }
            catch (Exception ex)
            {
                // If there is an exception while finding carriers.
                _logger.LogError("Error in GradientRate POST endpoint: Exception:{0}. Stack Trace: {1}", ex.Message, ex.StackTrace);
                response.Error?.Add(new Error
                {
                    Code = (int)HttpStatusCode.InternalServerError,
                    Description = !string.IsNullOrEmpty(ex.Message) ? "Something went wrong" : ex.Message,
                    Message = ex.Message
                });
                var gradientResponse = new GradientAIResponse { StatusCode = (int)HttpStatusCode.InternalServerError, Validation = new GradientValidation { Status = "Error", Error = ex.Message }, Message = ex.Message, HttpStatusCode = (int)HttpStatusCode.InternalServerError };
                response = HelperMethod.ResponseMapping<GradientAIResponse>((int)HttpStatusCode.InternalServerError, "Exception occurs while processing request.", gradientResponse, response.Error);
                return StatusCode((int)HttpStatusCode.InternalServerError, response);
            }
        }


        #region privateMethods




        /// <summary>
        /// CalculateSpecificWaiver
        /// </summary>
        /// <param name="wcSubmissionModel"></param>
        /// <param name="companyPlacementCode"></param>
        /// <returns></returns>
        private async Task<decimal?> CalculateSpecificWaiver(WCSubmissionV2 wcSubmissionModel, string companyPlacementCode)
        {
            _logger.LogInformation($"Calculate SpecificWaiver for. SubmissionId:{wcSubmissionModel.SubmissionId}");
            decimal? swtotal = 0;
            var primaryLocation = wcSubmissionModel?.LocationsClassifications?.FirstOrDefault(x => x.Location?.IsPrimary == true) ?? wcSubmissionModel?.LocationsClassifications?.FirstOrDefault();
            var state = primaryLocation?.Location?.State?.ToString();
            var blanketWaiverCol = await _service.GetBlanketWaiver(state ?? "");
            var specificWaiverCol = blanketWaiverCol.Where(x => x.Type == "Specific").FirstOrDefault();
            //todo  for cna territory not sure we have to use or not 
            //var territory = new Teritory();
            wcSubmissionModel.SWLocationsClassifications = wcSubmissionModel?.SWLocationsClassifications?.Count > 0 ? _helperMethod.SWSplitClassCodeMapping(wcSubmissionModel?.SWLocationsClassifications) : wcSubmissionModel?.SWLocationsClassifications;
            foreach (var locClass in wcSubmissionModel?.SWLocationsClassifications)
            {
                decimal? locSpecificWaiver = 0;
                foreach (var cl in locClass?.Classifications)
                {
                    //Appetite to do change

                    var appetiteList = await _service.GetAppetiteByStateAndClass(state, cl?.ClassCode?.ToString(), wcSubmissionModel.ProposedEffectiveDate);
                    var appetite = appetiteList.FirstOrDefault(x => x.CompanyCode == companyPlacementCode); // TO DO : need to figure out which appetite needs to be used from given list 
                    // previously there was only one rate was exist against each class code, but now there will be multiple.
                    decimal? amount = (appetite?.Rate * cl?.Payroll) / 100;
                    locSpecificWaiver += amount;
                }
                locSpecificWaiver = Math.Round((specificWaiverCol?.Factor * locSpecificWaiver) ?? 0, 3) > specificWaiverCol?.MinimumPremium ? Math.Round((specificWaiverCol?.Factor * locSpecificWaiver) ?? 0, 3) : specificWaiverCol?.MinimumPremium;
                swtotal += locSpecificWaiver;
            }
            return swtotal;
        }

        /// <summary>
        /// THis method is being used to validate Effective Date.
        /// </summary>
        /// <param name="effectiveDate">effectiveDate</param>
        /// <returns>true or false</returns>
        private ResponseViewModel<bool> ValidateEffectiveDate(DateTime? effectiveDate)
        {
            var response = new ResponseViewModel<bool>();
            DateTime maxEffectiveDate = DateTime.Today.AddDays(120);
            if (effectiveDate != null)
            {
                if (effectiveDate.Value.Date < DateTime.Today.AddDays(-1) || effectiveDate > maxEffectiveDate)
                {
                    _logger.LogWarning("Invalid effective date.Today : {Today}, effectiveDate : {effectiveDate}", DateTime.Today,effectiveDate);
                    response.Error?.Add(new Error
                    {
                        Code = (int)HttpStatusCode.BadRequest,
                        Description = "Policy date must be 1-120 days from today.",
                        Message = "Policy date must be 1-120 days from today."
                    });
                    response = HelperMethod.ResponseMapping<bool>((int)HttpStatusCode.BadRequest, "BadRequest", false, response.Error);
                }
            }
            return response;
        }

        /// <summary>
        /// ValidateClassCodes
        /// </summary>
        /// <param name="wcSubmissionModel"></param>
        /// <returns></returns>


        private async Task<ShortRatePenalties?> GetShortRatePenality(WCSubmissionV2 wcSubmissionModel, int? noOfDays, string? cancellationType)
        {
            var shortRatePenalty = new ShortRatePenalties { FactorForEarnedPremium = 1 };
            if (!string.IsNullOrEmpty(cancellationType) && cancellationType == "02")
            {
                _logger.LogInformation($"Calculate ShortRatePenalty from Rate POST endpoint. SubmissionId {wcSubmissionModel?.SubmissionId?.ToString()}");
                shortRatePenalty = await _service.GetShortRatePenalties(noOfDays);
            }

            return shortRatePenalty;
        }

        private async Task<RiskReservationResponse?> GetRiskReservation(WCSubmissionV2 wcSubmission, string token)
        {
            var governingClassifications = _helperMethod.GetGoverningLocation(wcSubmission);

            var governingClassCode = governingClassifications.OrderByDescending(m => m.Payroll).FirstOrDefault()?.ClassCode;
            var governingStateCode = governingClassifications.OrderByDescending(m => m.Payroll).FirstOrDefault()?.StateCode;
            var classCodeList = await _service.GetSubClassCode(governingStateCode, governingClassCode, wcSubmission.ProposedEffectiveDate ?? DateTime.Now);
            var classCode = classCodeList?.OrderByDescending(x => x.StartDate).FirstOrDefault(x => x.DefaultIndicator == 1) ?? classCodeList?.FirstOrDefault();
            if (classCode == null)
            {
                _logger.LogInformation("SIC Mapping not found for GoverningStateCode: {governingStateCode} GoverningClassCode: {governingClassCode} ProposedEffectiveDate: {wcSubmissionModel?.Submission.ProposedEffectiveDate}", governingStateCode, governingClassCode, wcSubmission?.ProposedEffectiveDate);
                return null;
            }
            return await _cnaV1Service.GetRiskReservation(_helperMethod.MapRiskReservationRequest(wcSubmission, classCode), token, wcSubmission.SubmissionId);

        }

        #region Validation
        private async Task<string> ValidateClassCodes(WCSubmissionV2 wcSubmissionModel)
        {
            _logger.LogInformation($"Validate ClassCodes for SubmissionId :{wcSubmissionModel?.SubmissionId?.ToString()}");
            string invalidClasscodes = "";
            for (int i = 0; i < wcSubmissionModel?.LocationsClassifications?.Count; i++)
            {
                for (int j = 0; j < wcSubmissionModel?.LocationsClassifications?[i]?.Classifications?.Count; j++)
                {
                    var classCode = wcSubmissionModel?.LocationsClassifications?[i]?.Classifications[j].ClassCode;
                    var stateCode = wcSubmissionModel?.LocationsClassifications?[i]?.Location?.State;
                    _logger.LogInformation("calling Appetite repository method.");
                    var classCodeList = await _service.GetAppetiteByStateAndClass(stateCode, classCode, wcSubmissionModel?.ProposedEffectiveDate);
                    // Check result is null or not and set the success property true or false. 
                    if (classCodeList == null || classCodeList.Count == 0)
                    {
                        invalidClasscodes += $"{stateCode} - {classCode}, ";
                    }
                }
            }
            return invalidClasscodes.Trim().TrimEnd(',');
        }

        private async Task<string> ValidateClassandCompanyCodes(WCSubmissionV2 wcSubmissionModel, string companyCode)
        {
            _logger.LogInformation($"Validate ClassCodes for SubmissionId :{wcSubmissionModel?.SubmissionId?.ToString()}");
            string invalidClasscodes = "";
            for (int i = 0; i < wcSubmissionModel?.LocationsClassifications?.Count; i++)
            {
                for (int j = 0; j < wcSubmissionModel?.LocationsClassifications?[i]?.Classifications?.Count; j++)
                {
                    var classCode = wcSubmissionModel?.LocationsClassifications?[i]?.Classifications[j].ClassCode;
                    var stateCode = wcSubmissionModel?.LocationsClassifications?[i]?.Location?.State;
                    _logger.LogInformation("calling Appetite repository method.");
                    var classCodeList = await _service.GetAppetiteByStateAndClass(stateCode!, classCode!, wcSubmissionModel?.ProposedEffectiveDate);
                    var appetiteBasedOnCompanyCode = classCodeList?.FirstOrDefault(x => x.CompanyCode == companyCode);
                    // Check result is null or not and set the success property true or false. 
                    if (classCodeList == null || classCodeList.Count == 0 || appetiteBasedOnCompanyCode == null)
                    {
                        invalidClasscodes += $"{stateCode} - {classCode} - {companyCode}, ";
                    }
                }
            }
            return invalidClasscodes.Trim().TrimEnd(',');
        }

        private bool ValidateInValidClasCode(string invalidClasscodeMesage, ResponseViewModel<WCResponse> response, out IActionResult? actionResult)
        {
            if (invalidClasscodeMesage != "")
            {
                _logger.LogWarning("classCodeList is null");
                response.Error?.Add(new Error
                {
                    Code = (int)HttpStatusCode.BadRequest,
                    Description = $"Appetite Failed:Class codes don't fit appetite {invalidClasscodeMesage}",
                    Message = $"Appetite Failed:Class codes don't fit appetite {invalidClasscodeMesage}"
                });
                response = HelperMethod.ResponseMapping<WCResponse>((int)HttpStatusCode.BadRequest, "Bad Request", null,
                    response.Error);
                {
                    actionResult = BadRequest(response);
                    return true;
                }
            }
            actionResult = null;
            return false;
        }

        private bool EndpointAuthorization(out ResponseViewModel<object> authenticationStatus, out IActionResult? statusCode)
        {
            _logger.LogInformation("Calling EndpointAuthorization method to validate JWT Token.");
            authenticationStatus = _tokenUtil.EndpointAuthorization(Request);
            if (authenticationStatus.Status != (int)HttpStatusCode.OK)
            {
                _logger.LogError("Request token is invalid");
                {
                    statusCode = StatusCode(authenticationStatus.Status, authenticationStatus);
                    return true;
                }
            }

            statusCode = null;
            return false;
        }

        private RatingCriteria RatingCriteriaRequest(WCSubmissionV2? wcSubmissionModel, decimal? schModifier,
            string? sourceType, WCLocationClassifications? primaryLocation, Dictionary<string, int>? classificationPayroll,
            decimal? specificWaiver, Configuration_Lookup config_look, [DisallowNull] decimal? minPremium, string? cancellationType, ShortRatePenalties? shortRatePenalty)
        {
            RatingCriteria objCalculationRequest = new()
            {
                LimitId = Convert.ToInt16(wcSubmissionModel?.LimitId),
                EffectiveDate = wcSubmissionModel.ProposedEffectiveDate.Value,
                State = primaryLocation?.Location?.State,
                ZipCode = Convert.ToInt32(primaryLocation?.Location.Zip),
                ProgramName = _configuration["ProgramName"],

                ClassificationPayrolls = classificationPayroll,
                BlanketWaiverSubrogation = wcSubmissionModel?.Applicant?.BlanketWaiverSubrogation,
                SpecificWaiver = wcSubmissionModel?.Applicant?.BlanketWaiverSubrogation != true ? specificWaiver ?? 0 : 0,
                XMod = wcSubmissionModel?.Applicant?.ExperienceMod != null && wcSubmissionModel?.Applicant?.ExperienceMod > 0
                    ? wcSubmissionModel?.Applicant?.ExperienceMod
                    : 1,
                MeritRating =
                       (wcSubmissionModel?.AdditionalFactor != null && wcSubmissionModel?.AdditionalFactor?.MeritRating != null &&
                     wcSubmissionModel?.AdditionalFactor?.MeritRating != 0) ? wcSubmissionModel?.AdditionalFactor?.MeritRating :
                    config_look?.MeritRating,
                DeductibleCredit =
                    (wcSubmissionModel?.AdditionalFactor != null &&
                     wcSubmissionModel?.AdditionalFactor?.DeductibleCredit != null)
                        ? wcSubmissionModel?.AdditionalFactor?.DeductibleCredit
                        : config_look?.DeductibleCredit,
                ScheduledRatingFactor = schModifier == null || schModifier == 0 ? 1 :
                    schModifier.Value <= config_look?.MaxSchForUW ? schModifier.Value : config_look.MaxSchForUW,
                ShortRatePenaltyFactor = cancellationType != null && cancellationType == "02" ?
                (shortRatePenalty != null && shortRatePenalty.FactorForEarnedPremium == 0 ? 1 : shortRatePenalty.FactorForEarnedPremium) : 1,
                Source = !string.IsNullOrEmpty(sourceType) && sourceType == "Audit" ? sourceType :
                    (schModifier == null || schModifier == 0) ? "API" : "UW",
                Minimum = minPremium,

                ConstructionClassification =
                    (wcSubmissionModel?.AdditionalFactor != null && wcSubmissionModel?.AdditionalFactor?.ConstructionClassification != null &&
                     wcSubmissionModel?.AdditionalFactor?.ConstructionClassification != 0) ? wcSubmissionModel?.AdditionalFactor?.ConstructionClassification :
                    config_look?.ConstructionClassification,
                WorkplaceSafetyCreditFactor =
                    (wcSubmissionModel?.AdditionalFactor != null && wcSubmissionModel?.AdditionalFactor?.WorkplaceSafetyCreditFactor != null &&
                     wcSubmissionModel?.AdditionalFactor?.WorkplaceSafetyCreditFactor != 0) ? wcSubmissionModel?.AdditionalFactor?.WorkplaceSafetyCreditFactor :
                    config_look?.WorkplaceSafetyCreditFactor,
                CertifiedSafetyCommitteePremiumFactor =
                    (wcSubmissionModel?.AdditionalFactor != null && wcSubmissionModel?.AdditionalFactor?.CertifiedSafetyCommitteePremiumFactor != null &&
                     wcSubmissionModel?.AdditionalFactor?.CertifiedSafetyCommitteePremiumFactor != 0) ? wcSubmissionModel?.AdditionalFactor?.CertifiedSafetyCommitteePremiumFactor :
                    config_look?.CertifiedSafetyCommitteePremiumFactor,
                OutstandingRateDecrease =
                    (wcSubmissionModel?.AdditionalFactor?.OutstandingRateDecrease != null &&
                     wcSubmissionModel?.AdditionalFactor?.OutstandingRateDecrease != 0) ? wcSubmissionModel?.AdditionalFactor?.OutstandingRateDecrease :
                    config_look?.OutstandingRateDecrease,
                OutstandingRateIncrease =
                    (wcSubmissionModel?.AdditionalFactor?.OutstandingRateIncrease != null &&
                     wcSubmissionModel?.AdditionalFactor?.OutstandingRateIncrease != 0) ? wcSubmissionModel?.AdditionalFactor?.OutstandingRateIncrease :
                    config_look?.OutstandingRateIncrease,
            };
            return objCalculationRequest;
        }

        private bool ValidateConfigLookUp(WCSubmissionV2 wcSubmissionModel, Configuration_Lookup config_look,
            WCLocationClassifications primaryLocation, ResponseViewModel<WCResponse> response, out IActionResult configResult)
        {
            if (config_look == null)
            {
                _logger.LogWarning(
                    $"No record found from Configuration_Lookup service for state: {primaryLocation?.Location?.State} and EffectiveDate : {wcSubmissionModel?.ProposedEffectiveDate.ToString()}");
                response.Error?.Add(new Error
                {
                    Code = (int)HttpStatusCode.NotFound,
                    Description =
                        $"No record found from Configuration_Lookup service for state: {primaryLocation?.Location?.State} and EffectiveDate : {wcSubmissionModel?.ProposedEffectiveDate.ToString()}",
                    Message =
                        $"No record found from Configuration_Lookup service for state: {primaryLocation?.Location?.State} and EffectiveDate : {wcSubmissionModel?.ProposedEffectiveDate.ToString()}"
                });
                response = HelperMethod.ResponseMapping<WCResponse>((int)HttpStatusCode.NotFound, "Not found", null,
                    response.Error);
                {
                    configResult = NotFound(response);
                    return true;
                }
            }
            configResult = null;
            return false;
        }

        private bool ValidateRiskAndCompanyCode(WCSubmissionV2? wcSubmissionModel, ResponseViewModel<WCResponse> response, out string? companyPlacementCode,
            out IActionResult? riskCompanyResult)
        {
            companyPlacementCode = string.Empty;
            if (wcSubmissionModel!.KeyValues != null)
            {
                //var riskStatus = wcSubmissionModel.KeyValues.Where(x => x.Key == "RiskStatus").Select(y => y.Value)
                //    .FirstOrDefault();
                //if (riskStatus != "true")
                //{
                //    _logger.LogError("Risk reservation check failed {SubmissionId}", wcSubmissionModel?.SubmissionId);
                //    response.Error?.Add(new Error
                //    {
                //        Code = (int)HttpStatusCode.BadRequest,
                //        Description = "Risk reservation check failed.",
                //        Message = "Risk reservation check failed."
                //    });
                //    response = HelperMethod.ResponseMapping<WCResponse>((int)HttpStatusCode.BadRequest,
                //        "BadRequest : Risk reservation check failed.", null, response.Error);
                //    {
                //        riskCompanyResult = BadRequest(response);
                //        return true;
                //    }
                //}

                companyPlacementCode = wcSubmissionModel.KeyValues.Where(x => x.Key == "CompanyCode").Select(y => y.Value).FirstOrDefault();
            }
            riskCompanyResult = null;
            return false;
        }

        private bool ValidateModelState(ResponseViewModel<WCResponse> response, out IActionResult? modelState)
        {
            if (!ModelState.IsValid)
            {
                _logger.LogWarning("Bad Request : Rate POST Invalid Model");
                foreach (var e in ModelState.SelectMany(x => x.Value!.Errors))
                {
                    response.Error?.Add(new Error()
                    { Message = e.ErrorMessage, Description = e.ErrorMessage, Code = (int)HttpStatusCode.BadRequest });
                }

                response = HelperMethod.ResponseMapping<WCResponse>((int)HttpStatusCode.BadRequest, "Bad Request", null,
                    response.Error);
                {
                    modelState = BadRequest(response);
                    return true;
                }
            }
            modelState = null;
            return false;
        }

        private bool ValidateLocationClassification(WCSubmissionV2 wcSubmissionModel, ResponseViewModel<WCResponse> response, out IActionResult? result)
        {
            if (wcSubmissionModel.LocationsClassifications!.Count <= 0)
            {
                _logger.LogError($"Bad Request. LocationClassification is null in provided model");
                response.Error?.Add(new Error
                {
                    Code = (int)HttpStatusCode.BadRequest,
                    Description = "Please provide LocationClassification.",
                    Message = "Please provide LocationClassification."
                });
                response = HelperMethod.ResponseMapping<WCResponse>((int)HttpStatusCode.BadRequest,
                    "Bad Request. LocationClassification is not provided in request.", null, response.Error);
                {
                    result = BadRequest(response);
                    return true;
                }
            }

            if (wcSubmissionModel.LocationsClassifications!.Count > 1)
            {
                var states = wcSubmissionModel.LocationsClassifications.Select(lc => lc.Location.State).Distinct().ToList();
                if (states.Count > 1)
                {
                    _logger.LogError($"Bad Request. Multiple location classifications in different states are not supported.");
                    response.Error?.Add(new Error
                    {
                        Code = (int)HttpStatusCode.BadRequest,
                        Description = "Multiple location classifications in different states are not supported.",
                        Message = "Multiple location classifications in different states are not supported."
                    });
                    response = HelperMethod.ResponseMapping<WCResponse>((int)HttpStatusCode.BadRequest,
                        "Bad Request. Multiple location classifications in different states are not supported.", null, response.Error);
                    result = BadRequest(response);
                    return true;
                }
            }

            result = null;
            return false;
        }
        private async Task<GradientAIRequest> MapGradientAIData(WCSubmissionV2 wcSubmissionModel, string? submissionId, string? PolicyNumber)
        {
            var primaryLocationClassifications = wcSubmissionModel.LocationsClassifications?.Find(x => x.Location?.IsPrimary == true) ?? wcSubmissionModel.LocationsClassifications?.FirstOrDefault();
            var companyPlacementCode = wcSubmissionModel.KeyValues?.Where(x => x.Key == "CompanyCode").Select(y => y.Value).FirstOrDefault();
            if (string.IsNullOrEmpty(companyPlacementCode))
            {
                var company = await _service.GetDefaultCompany(primaryLocationClassifications?.Location.State);
                companyPlacementCode = company?.Code;

            }
            var classCodes = primaryLocationClassifications?.Classifications.Select(x => x.ClassCode).Distinct().ToArray();
            var appetites = await _service.GetAppetites(primaryLocationClassifications?.Location?.State!, classCodes, wcSubmissionModel.ProposedEffectiveDate, companyPlacementCode);
            List<ClassCodeFactor>? classCodeFactors = new();
            if (primaryLocationClassifications?.Classifications != null)
            {
                foreach (var classification in primaryLocationClassifications.Classifications)
                {
                    classCodeFactors.Add(new ClassCodeFactor()
                    {
                        ClassCode = classification.ClassCode,
                        Payroll = classification.Payroll,
                        BaseClassCodeRate = appetites?.Where(a => a.ClassCode == classification.ClassCode).FirstOrDefault()?.Rate
                    });
                }
            }
            List<InsuredLocationRequest> insuredLocationResponses = new()
            {
                new InsuredLocationRequest()
                {
                    ClassCodeFactors = classCodeFactors,
                    PostalCode = primaryLocationClassifications?.Location?.Zip,
                    State = primaryLocationClassifications?.Location?.State
                }
            };
            GradientAIRequest gradientAIRequest = new()
            {
                EffectiveDate = wcSubmissionModel.ProposedEffectiveDate.Value.ToString("yyyy-MM-dd"),
                ExpirationDate = wcSubmissionModel.ProposedExpirationDate?.ToString("yyyy-MM-dd") ?? wcSubmissionModel.ProposedEffectiveDate.Value.AddYears(1).ToString("yyyy-MM-dd"),
                InsuredID = submissionId,
                InsuredName = !string.IsNullOrEmpty(wcSubmissionModel.Applicant?.LegalEntityName) ? wcSubmissionModel.Applicant?.LegalEntityName : $"{wcSubmissionModel.Applicant?.InsuredFirstName} {wcSubmissionModel.Applicant?.InsuredLastName}",
                InsuredTaxID = wcSubmissionModel.Applicant?.FEIN,
                PolicyNumber = PolicyNumber,
                InsuredLocations = insuredLocationResponses,
                PolicyID = $"{submissionId}-{primaryLocationClassifications?.Location?.State}-{wcSubmissionModel.ProposedEffectiveDate.Value.Year:YY}"

            };
            return gradientAIRequest;

        }
        #endregion

        #endregion
    }


}