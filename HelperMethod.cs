using BTIS.DotNetLogger.Standard;
using BTIS.GeneralLiability;
using BTIS.Linq;
using BTIS.Utility.Standard;
using CNA.V2.Domain.DTO;
using CNA.V2.Domain.Enums;
using CNA.V2.Domain.Model;
using CNA.V2.Domain.Model.CompanyPlacement;
using CNA.V2.Domain.Model.RiskReservation;
using CNA.V2.Domain.ResponseModel;
using CNA.V2.Service.Services;
using CNA.V2.Service.Services.Interface;
using CNA.V2.Service.Utility;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Driver.Linq;
using Newtonsoft.Json;
using Org.BouncyCastle.Security.Certificates;
using System.Net;
using System.Text.RegularExpressions;
using CalculationCriteria = CNA.V2.Domain.Model.CalculationCriteria;
using IHostingEnvironment = Microsoft.AspNetCore.Hosting.IWebHostEnvironment;

namespace CNA.V2.Service.Helper
{
    public class HelperMethod : IHelperMethod
    {
        #region  --Inject Dependency--
        private readonly ITokenUtility _tokenUtil;
        private readonly ICorrelationIdProvider _correlationIdProvider;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IHostingEnvironment _hostingEnvironment;
        private readonly IConfiguration _configuration;
        private readonly ILogger _logger;
        private readonly SmartHttpClient _httpClient;
        private readonly IService _service;


        public HelperMethod(ITokenUtility tokenUtility,
            IHostingEnvironment hostingEnvironment,
            IConfiguration configuration,
        ILogger<HelperMethod> logger, ICorrelationIdProvider correlationIdProvider,
        IHttpContextAccessor httpContextAccessor, IService service)
        {
            _correlationIdProvider = correlationIdProvider;
            _httpContextAccessor = httpContextAccessor;
            _httpClient = new SmartHttpClient(_correlationIdProvider, _httpContextAccessor);
            _tokenUtil = tokenUtility;
            _hostingEnvironment = hostingEnvironment;
            _configuration = configuration;
            _logger = logger;
            _service = service;
        }
        #endregion

        public static ResponseViewModel<T> ResponseMapping<T>(int status, string? statusDescription, T? response, List<Error>? error)
        {
            ResponseViewModel<T> res = new ResponseViewModel<T>
            {
                Status = status,
                Message = statusDescription,
                Response = response,
                Error = error
            };
            return res;
        }

        /// <summary>
        /// This method is used to Authorize for those endpoints which are available only for BTIS.
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        public Task<ResponseViewModel<object>> EndpointAuthorization(HttpRequest request)
        {
            _ = new ResponseViewModel<object>();
            // Checking if token is present or not in request.
            var tokenString = _tokenUtil.GetTokenFromHeader(request.Headers).ToString();
            ResponseViewModel<object>? response;
            // Checking if token is valid.
            if (!_tokenUtil.IsValidToken(tokenString))
            {
                // If the token is not authenticate.
                response = ResponseMapping<object>((int)HttpStatusCode.Unauthorized, "Unauthorized - Invalid Token", null, null);
                return Task.FromResult(response);
            }
            response = ResponseMapping<object>((int)HttpStatusCode.OK, "Success", null, null);
            return Task.FromResult(response);
        }

        /// <summary>
        /// MapLocationClassfication
        /// </summary>
        /// <param name="location"></param>
        /// <returns></returns>
        public List<WCLocationClassifications> MapLocationClassfication(List<WCLocationClassifications> location)
        {
            var splitClasscodeMapping = JsonConvert.DeserializeObject<List<SplitClassCodeMapping>>(_configuration["SplitClassCodeMapping"]);
            for (int i = 0; i < location?.Count; i++)
            {
                var isAvailable = splitClasscodeMapping!.Exists(x => x.State?.ToLower() == location?[i]?.Location?.State?.ToLower());
                if (isAvailable)
                {
                    for (int j = 0; j < location?[i]?.Classifications?.Count; j++)
                    {
                        var classcode = splitClasscodeMapping.Where(x => x.State?.ToLower() == location?[i]?.Location?.State?.ToLower() && x.ClassCode?.ToLower() == location?[i]?.Classifications?[j]?.ClassCode?.ToLower()).Select(x => x.TargetClassCode).FirstOrDefault();
                        if (!string.IsNullOrEmpty(classcode))
                            location[i].Classifications[j].ClassCode = classcode;
                    }
                }
            }
            return location!;
        }

        /// <summary>
        /// GetMinimumPremiumForClass
        /// </summary>
        /// <param name="wcSubmissionModel"></param>
        /// <param name="noOfDays"></param>
        /// <returns></returns>
        public async Task<decimal> GetMinimumPremiumForClass(WCSubmissionV2 wcSubmissionModel, int? noOfDays, string companyPlacementCode)
        {
            var primaryLocation = wcSubmissionModel?.LocationsClassifications?.FirstOrDefault(x => x.Location?.IsPrimary == true) ?? wcSubmissionModel?.LocationsClassifications?.FirstOrDefault();
            var classCodes = primaryLocation!.Classifications.Select(x => x.ClassCode).Distinct().ToArray();
            var appetites = await _service.GetAppetites(primaryLocation?.Location?.State!, classCodes, wcSubmissionModel?.ProposedEffectiveDate, companyPlacementCode);
            var minimumPremium = appetites?.Where(x => x.MinimumPremium == appetites?.Max(y => y.MinimumPremium)).Select(y => y.MinimumPremium).FirstOrDefault();
            if (wcSubmissionModel!.KeyValues != null)
            {
                var prorateData = wcSubmissionModel.KeyValues.Where(x => x.Key == "ProrateAmount").Select(y => y.Value).FirstOrDefault();
                if (prorateData != null && prorateData == "Yes")
                {
                    minimumPremium = Math.Round(CalculateProRateMinPremium(minimumPremium, wcSubmissionModel.ProposedExpirationDate, wcSubmissionModel.ProposedEffectiveDate, noOfDays), 0);
                }
            }
            return minimumPremium != null ? minimumPremium.Value : 0;
        }

        /// <summary>
        /// CalculateProRateMinPremium
        /// </summary>
        /// <param name="mimPre"></param>
        /// <param name="proposedExpirationDate"></param>
        /// <param name="proposedEffectiveDate"></param>
        /// <param name="noOfDays"></param>
        /// <returns></returns>
        public decimal CalculateProRateMinPremium(decimal? mimPre, DateTime? proposedExpirationDate, DateTime? proposedEffectiveDate, int? noOfDays) => (mimPre / ((decimal)(proposedExpirationDate - proposedEffectiveDate)?.TotalDays)) * noOfDays ?? 0;

        /// <summary>
        /// CalculateProRateExpenseConstant
        /// </summary>
        /// <param name="model"></param>
        /// <param name="config_Lookup"></param>
        /// <param name="noOfDays"></param>
        /// <returns></returns>
        public Task<decimal> CalculateProRateExpenseConstant(WCSubmissionV2 model, Configuration_Lookup config_Lookup, int? noOfDays)
        {
            decimal expenseConstant = config_Lookup.Expense_Constant;
            if (model.KeyValues != null)
            {
                var prorateData = model.KeyValues.Where(x => x.Key == InputKeys.ProrateExpenseConstant.ToString()).Select(y => y.Value).FirstOrDefault();
                if (prorateData != null && prorateData.ToString() == "Yes")
                {
                    expenseConstant = (config_Lookup.Expense_Constant / ((decimal)(model.ProposedExpirationDate - model.ProposedEffectiveDate)?.TotalDays)) * noOfDays ?? 0;
                    expenseConstant = config_Lookup.Expense_Constant == 0 ? 0 : expenseConstant > Constant.MinExpenseConstant ? Math.Round(expenseConstant, 0) : Constant.MinExpenseConstant;
                }
            }
            return Task.FromResult(expenseConstant);
        }

        /// <summary>
        /// Returns N part of number.
        /// </summary>
        /// <param name="number">Initial number</param>
        /// <param name="n">Number of digits required</param>
        /// <returns>First part of number</returns>
        public static int TakeNDigits(int number, int n)
        {
            // this is for handling negative numbers, we are only interested in positive number
            number = Math.Abs(number);
            // special case for 0 as Log of 0 would be infinity
            if (number == 0)
                return number;
            // getting number of digits on this input number
            int numberOfDigits = (int)Math.Floor(Math.Log10(number) + 1);
            // check if input number has more digits than the required get first N digits
            if (numberOfDigits >= n)
                return (int)Math.Truncate((number / Math.Pow(10, numberOfDigits - n)));
            else
                return number;
        }

        public List<SWLocationsClassifications> SWSplitClassCodeMapping(List<SWLocationsClassifications> location)
        {
            var splitClassCodeJson = JsonConvert.DeserializeObject<List<SplitClassCodeMapping>>(_configuration["SplitClassCodeMapping"]);
            for (int i = 0; i < location?.Count(); i++)
            {
                if (splitClassCodeJson.Any(a => a.State?.ToLower() == location?[i]?.Location?.State?.ToLower()))
                {
                    for (int j = 0; j < location?[i]?.Classifications?.Count(); j++)
                    {
                        string classCode = splitClassCodeJson.Where(s => s.State?.ToLower() == location?[i]?.Location?.State?.ToLower() && s.ClassCode?.ToLower() == location?[i]?.Classifications?[j]?.ClassCode?.ToLower()).Select(w => w.TargetClassCode).FirstOrDefault();
                        if (!string.IsNullOrEmpty(classCode))
                            location[i].Classifications[j].ClassCode = classCode;
                    }
                }
            }
            return location;
        }

        /// <summary>
        /// Method is used to calculate schedule modifier.
        /// </summary>
        /// <param name="model"></param>
        /// <param name="maxSchForAgent"></param>
        public decimal CalculateScheduleModifier(List<QuestionResponse> model, decimal maxSchForAgent)
        {
            var maxSchmdByBtis = JsonConvert.DeserializeObject<decimal>(_configuration["MaxSchmdByBtis"]); //0.75M;
            decimal schMod = Convert.ToDecimal(1 - ((model ?? new List<QuestionResponse>()).Count(qr => (qr.Answer ?? "").ToLower() == "yes") * 0.05));
            //schMod = schMod >= maxSchForAgent ? schMod : maxSchForAgent;   //current 
            schMod = Math.Max(maxSchmdByBtis, Math.Max(maxSchForAgent, schMod));   ////  new changes just added.
            return schMod;
        }

        /// <summary>
        /// THis method is being used to map wcresponse object.
        /// </summary>
        /// <param name="location"></param>
        /// <param name="originalRatingDetails"></param>
        /// <param name="ratingDetails"></param>
        /// <param name="effDate"></param>
        /// <param name="minimumPremiumAllClasses"></param>
        /// <param name="scheduledRatingFactor"></param>
        /// <returns></returns>
        public WCResponse WCResponseMapping(WCLocationClassifications location, RatingWorksheet originalRatingDetails, RatingWorksheet ratingDetails, DateTime effDate, decimal? minimumPremiumAllClasses, decimal scheduledRatingFactor,string companyPlacementCode,string submissionId,string ratingDetailsId)
        {
            string companyName = _configuration["CompanyName"];
            var wcresponse = new WCResponse
            {
                success = true,
                quote =
                {
                    criteria =
                    {
                        effectiveDate = effDate,
                        state = location?.Location?.State,
                        zip = location?.Location?.Zip,
                        limits = null,
                        optional_coverages = null,
                        policy_discounts = null,
                        classification_payrolls = (location?.Classifications??new List<WCClassifications>()).Select(a => new ClassificationPayroll
                        {
                            classcode = a.ClassCode,
                            classcodedescription = a.Description,
                            fulltimeemployees = Convert.ToInt32(a.NumberOfFullTimeEmployees),
                            partimeemployees = Convert.ToInt32(a.NumberOfPartTimeEmployees),
                            payroll = Convert.ToInt32(a.Payroll)
                        }).ToList(),
                        receipts = null,
                        CompanyCode = companyPlacementCode
                    },
                    results =
                    {
                        type = null,
                        total_premium = 0,
                        total_earned = 0,
                        line_items = GetLineItems(originalRatingDetails, ratingDetails,minimumPremiumAllClasses)
                    },
                },

                IsBindEligible = false,
                submissionid = submissionId,
                ratingdetailsId = ratingDetailsId,
                carrier = companyName,
                SubmissionStatus = "Quoted",
                ReferenceNumber = submissionId,
                SchModifier = scheduledRatingFactor
            };
            wcresponse.quote.results.total_premium = Convert.ToInt32(wcresponse.quote.results.line_items.WC.Where(a => a.type == "Premium" || a.type == "Taxes" || a.type == "Policy_fee").Sum(a => a.total_premium));
            return wcresponse;
        }

        private static LineItems GetLineItems(RatingWorksheet originalRatingDetails, RatingWorksheet ratingDetails, decimal? minimumPremiumAllClasses)
        {
            var rdPhase = ratingDetails?.Rating?.Phases?.Reverse();
            var ordPhase = originalRatingDetails?.Rating?.Phases?.Reverse();

            var taxes = originalRatingDetails.Taxes;

            //var currentPolicyFee = rdPhase?.Skip(1).FirstOrDefault();
            // var originalPolicyFee = ordPhase?.Skip(1).FirstOrDefault();

            var currentPremium = ratingDetails != null && Convert.ToInt32(rdPhase?.Skip(2)?.FirstOrDefault()?.Premium?.Value) > minimumPremiumAllClasses ? Convert.ToInt32(rdPhase?.Skip(2)?.FirstOrDefault()?.Premium?.Value) : minimumPremiumAllClasses.Value;
            decimal originalPremium = GetOriginalPremium(originalRatingDetails, minimumPremiumAllClasses, ordPhase);

            LineItems lineitems = new()
            {
                WC = new List<WC>
                {
                    new WC
                    {
                        type = "Premium",
                        total_premium = currentPremium,
                        total_earned = 0,
                    },
                    new WC
                    {
                        type = "Taxes",
                        total_premium = Convert.ToInt32(CalculatePremiumTax(taxes??new List<Taxes>(), Convert.ToInt32(currentPremium))),
                        total_earned = 0
                    },
                    //new WC
                    //{
                    //    type = "Policy_fee",
                    //    total_premium = Convert.ToInt32(currentPolicyFee?.Premium?.Value),
                    //    total_earned = 0
                    //}
                },
                Carrier = new List<WC>
                {
                   new WC
                    {
                        type = "Premium",
                        total_premium = originalPremium,
                        total_earned = 0,
                    },
                    new WC
                    {
                        type = "Taxes",
                        total_premium = Convert.ToInt32(CalculatePremiumTax(taxes??new List<Taxes>(), Convert.ToInt32(originalPremium))),
                        total_earned = 0
                    },
                    //new WC
                    //{
                    //    type = "Policy_fee",
                    //    total_premium = Convert.ToInt32(originalPolicyFee?.Premium?.Value),
                    //    total_earned = 0

                    //}
                }
            };
            foreach (var tax in ratingDetails?.Taxes)
            {
                lineitems.WC.Add(new WC { type = tax.ItemName, total_premium = CalculatePremiumTax(Convert.ToInt32(currentPremium), tax?.Percentage ?? 0) });
                lineitems.Carrier.Add(new WC { type = tax.ItemName, total_premium = CalculatePremiumTax(Convert.ToInt32(originalPremium), tax?.Percentage ?? 0) });
            }
            return lineitems;
        }

        public static decimal GetOriginalPremium(WC_ratings originalRatingDetails, decimal? minimumPremiumAllClasses, IEnumerable<BTIS.Rating.DTO.RatingPhase<string, Hazards>>? ordPhase)
        {
            return originalRatingDetails != null && Convert.ToInt32(ordPhase?.Skip(2)?.FirstOrDefault()?.Premium?.Value) > minimumPremiumAllClasses ? Convert.ToInt32(ordPhase?.Skip(2)?.FirstOrDefault()?.Premium?.Value) : minimumPremiumAllClasses.Value;
        }

        public static decimal GetOriginalPremium(RatingWorksheet ratingWorksheet, decimal? minimumPremiumAllClasses, IEnumerable<BTIS.Rating.DTO.RatingPhase<string, Hazards>>? ordPhase)
        {
            return ratingWorksheet != null && Convert.ToInt32(ordPhase?.Skip(2)?.FirstOrDefault()?.Premium?.Value) > minimumPremiumAllClasses ? Convert.ToInt32(ordPhase?.Skip(2)?.FirstOrDefault()?.Premium?.Value) : minimumPremiumAllClasses.Value;
        }

        /// <summary>
        /// Method is used to calculate taxes.
        /// </summary>
        /// <param name="taxes"></param>
        /// <param name="premiumAmount"></param>
        private static decimal CalculatePremiumTax(List<Taxes> taxes, int premiumAmount)
        {
            decimal taxAmt = 0;
            foreach (var tax in taxes)
            {
                taxAmt = Convert.ToInt32(taxAmt + (tax.Percentage / 100) * premiumAmount);
            }
            return taxAmt;
        }

        /// <summary>
        /// Method is used to calculate taxes.
        /// </summary>
        /// <param name="percentage"></param>
        /// <param name="premiumAmount"></param>
        private static decimal CalculatePremiumTax(int premiumAmount, decimal percentage) => Convert.ToInt32((percentage / 100) * premiumAmount);

        public async Task<WCResponse> GetGoverningClassCode(string state, WC_ratings wcrating, WCResponse wcresponse)
        {

            string lineItemsString = string.Empty;
            var rateItemsString = string.Empty;
            //if (state == "CA")
            //{
            //    lineItemsString = JsonConvert.SerializeObject(wcrating?.RatingWorksheet?.Rating?.Phases?[5].LineItems.Items);
            //    rateItemsString = JsonConvert.SerializeObject(wcrating?.RatingWorksheet?.Rates?.BaseRates?.Items);
            //}
            //if (state == "PA")
            //{
            //    lineItemsString = JsonConvert.SerializeObject(wcrating?.RatingWorksheet?.Rating?.Phases?[4].LineItems.Items);
            //    rateItemsString = JsonConvert.SerializeObject(wcrating?.RatingWorksheet?.Rates?.BaseRates?.Items);
            //}
            //else
            //{
            lineItemsString = JsonConvert.SerializeObject(wcrating?.RatingWorksheet?.Rating?.Phases?[4].LineItems.Items);
            rateItemsString = JsonConvert.SerializeObject(wcrating?.RatingWorksheet?.Rates?.BaseRates?.Items);
            //}

            var ratingItems = JsonConvert.DeserializeObject<List<RatingItem>>(lineItemsString);
            var baseRateItems = JsonConvert.DeserializeObject<List<BaseRateItems>>(rateItemsString);

            // check class code with same premium for multiple class
            var duplicatePremiumItems = ratingItems.GroupBy(t => new { t.Premium }).Where(t => t.Count() > 1).SelectMany(x => x).ToList();
            var duplicatePremium = Convert.ToInt32(duplicatePremiumItems.Select(p => p.Premium).FirstOrDefault());

            // get max premium amount
            var maxPremium = ratingItems.Where(x => x.Premium == ratingItems.Max(y => y.Premium)).Select(x => x.Premium).FirstOrDefault();
            var governingClassCode = string.Empty;

            if (duplicatePremiumItems != null && duplicatePremiumItems.Count > 1 && duplicatePremium > Convert.ToInt32(maxPremium))
                governingClassCode = baseRateItems.Where(b => b.Tiers == baseRateItems.Max(t => t.Tiers)).Select(b => b.Classification).FirstOrDefault();

            else
                governingClassCode = ratingItems.Where(x => x.Premium == ratingItems.Max(y => y.Premium)).Select(x => x.Name).FirstOrDefault();

            //var naicsSicCode = await _service.GetNAICSSICCode(state, governingClassCode);
            //if (wcresponse != null & naicsSicCode != null)
            //{
            //    wcresponse.GoverningClassCode = naicsSicCode.ClassCode;
            //    wcresponse.NAICS_Code = naicsSicCode.NAICS;
            //    wcresponse.SIC_Code = naicsSicCode.SIC;
            //}
            return wcresponse;
        }


        /// <summary>
        /// Method is used to map Calculation DOM.
        /// </summary>
        /// <param name="reportData">CalculationReportData</param>
        /// <param name="criteria">RatingCriteria</param>
        /// <param name="expenseConstant">ExpenseConstant</param>
        public CalculationData MapCalculationData(CalculationReportData reportData, RatingCriteria criteria, decimal? expenseConstant)
        {
            CalculationData calculationData = new()
            {
                LossCostMultiplier = reportData?.Configuration_Lookup?.LossCostMultiplier ?? 0,
                ExpenseConstant = expenseConstant ?? 0,
                TerrorismRate = reportData?.TerrorismFactor ?? 0,
                CatastropheRate = reportData?.CatastropheFactor ?? 0,
                BlanketWaiverOfSubrogation = criteria.BlanketWaiverSubrogation == true ? MapBlanketWaiver(reportData, criteria) : new ChargeModel(),
                IncreasedLimitsForEmployersLiability = MapLimit(reportData, criteria),
                ScheduledRatingFactor = criteria.ScheduledRatingFactor == 0M ? 1 : criteria.ScheduledRatingFactor,
                ClassCodeBaseRates = MapAppetite(reportData?.Appetites),
                TerritoryFactors = MapTerrirory(reportData?.Teritory),

                StandardPremiumDiscounts = MapStandardPremium(reportData?.Premium_Discounts),
                PolicyAdministrationFees = MapPAFees(reportData?.PolicyFees),
                ClassificationPayrolls = criteria?.ClassificationPayrolls,
                EffectiveDate = criteria.EffectiveDate,
                LimitId = criteria.LimitId,
                State = criteria.State,
                XMod = criteria.XMod,
                ZipCode = criteria.ZipCode,
                SpecificWaiver = criteria?.BlanketWaiverSubrogation != true ? criteria?.SpecificWaiver : 0,
                ShortRatePenaltyFactor = criteria?.ShortRatePenaltyFactor,
                Minimum = criteria.Minimum,
                AuditNonComplianceCharge = 0,// reportData?.Configuration_Lookup.AuditNonComplianceCharge ?? 0,
                MeritRating = criteria?.MeritRating != null ? criteria?.MeritRating : reportData?.Configuration_Lookup?.MeritRating,
                DeductibleCredit = (criteria?.DeductibleCredit != null && criteria?.DeductibleCredit != 0) ? criteria?.DeductibleCredit : reportData.Configuration_Lookup.DeductibleCredit,
                BTISServiceFee = MapBTISServiceFees(reportData?.PolicyFees, criteria?.ClassificationPayrolls),
                TotalMinimum = (criteria != null && criteria?.Minimum != null ? criteria?.Minimum : 0) + (reportData?.Limit?.MinimumPremium ?? 0),
                WorkplaceSafetyCreditFactor = criteria?.WorkplaceSafetyCreditFactor != null && criteria.WorkplaceSafetyCreditFactor != 0 ? criteria.WorkplaceSafetyCreditFactor.Value : reportData.Configuration_Lookup.WorkplaceSafetyCreditFactor,
                ConstructionClassification = criteria?.ConstructionClassification != null && criteria.ConstructionClassification != 0 ? criteria.ConstructionClassification.Value : reportData.Configuration_Lookup.ConstructionClassification,
                CertifiedSafetyCommitteePremiumFactor = criteria?.CertifiedSafetyCommitteePremiumFactor != null && criteria.CertifiedSafetyCommitteePremiumFactor != 0 ? criteria.CertifiedSafetyCommitteePremiumFactor.Value : reportData.Configuration_Lookup.CertifiedSafetyCommitteePremiumFactor ?? 0,
                OutstandingRateDecrease = criteria?.OutstandingRateDecrease ?? reportData?.Configuration_Lookup?.OutstandingRateDecrease ?? 0,
                OutstandingRateIncrease = criteria?.OutstandingRateIncrease ?? reportData?.Configuration_Lookup?.OutstandingRateIncrease ?? 0,
                ForeignVoluntaryCoverage = criteria?.ForeignVoluntaryCoverage ?? reportData?.Configuration_Lookup?.ForeignVoluntaryCoverage ?? 1,
                AdditionalDefenseCoverage = criteria?.AdditionalDefenseCoverage ?? reportData?.Configuration_Lookup?.AdditionalDefenseCoverage ?? 0,

            };
            return calculationData;
        }
        private static ChargeModel MapBlanketWaiver(CalculationReportData reportData, RatingCriteria criteria)
        {
            return new ChargeModel()
            {
                Factor = reportData?.BlanketWaiver?.Factor ?? 0,
                Minimum = reportData?.BlanketWaiver?.MinimumPremium ?? 0
            };
        }
        private static ChargeModel MapLimit(CalculationReportData reportData, RatingCriteria criteria)
        {
            return new ChargeModel()
            {
                Factor = reportData?.Limit?.Factor ?? 0,
                Minimum = reportData?.Limit?.MinimumPremium ?? 0
            };
        }

        private static List<RangeModel> MapBTISServiceFees(List<PolicyFee> policyFees, Dictionary<string, int> ClassificationPayrolls) //=>
        {
            List<RangeModel> fees = new List<RangeModel>();
            if (policyFees == null || policyFees.Count == 0)
                fees = new List<RangeModel>();
            else
            {
                decimal payrollSum = ClassificationPayrolls.Select(a => a.Value).Sum();
                if (payrollSum > 0)
                    fees.AddRange(policyFees.Select(a => new RangeModel(0, 0, 0) { Min = a.StartPremium, Max = a.EndPremium, Value = a.Amount }));
                else
                    fees.Add(new RangeModel(0, 0, 0) { Min = 0, Max = 1000000, Value = 200 });
            }
            return fees;
        }
        private static Dictionary<string, decimal> MapTerrirory(Teritory teritory)
        {
            Dictionary<string, decimal> dict = new Dictionary<string, decimal>();
            if (teritory != null)
                dict.Add(teritory.Territory.ToString(), teritory.ActualPer);

            return dict;
        }
        private static Dictionary<string, decimal> MapAppetite(List<Appetite> appetites) =>
            appetites?.ToDictionary(a => a?.ClassCode, a => a?.Rate ?? 0);

        private static List<RangeModel> MapStandardPremium(List<Premium_Discount> discounts) =>
            discounts.Select(a => new RangeModel(0, 0, 0) { Min = a.StartPremium, Max = a.EndPremium, Value = a.Percentage_Discount }).ToList();

        private static List<RangeModel> MapPAFees(List<PolicyFee> policyFees) =>
            policyFees.Select(a => new RangeModel(0, 0, 0) { Min = a.StartPremium, Max = a.EndPremium, Value = a.Amount }).ToList();

        public static Domain.DTO.CalculationCriteria MapCalculationCriteriaDTO(CalculationCriteria calculationCriteria)
        {
            return new Domain.DTO.CalculationCriteria()
            {
                Payrolls = calculationCriteria.Payrolls.ToDTO(),
                BaseRates = calculationCriteria.BaseRates.ToDTO(),
                IncreasedLimitsForEmployersLiabilityFactor = calculationCriteria.IncreasedLimitsForEmployersLiabilityFactor.ToDTO(),
                BlanketWaiverOfSubrogation = calculationCriteria.BlanketWaiverOfSubrogation.ToDTO(),
                SpecificWaiver = calculationCriteria.SpecificWaiver.ToDTO(),
                Xmod = calculationCriteria.Xmod.ToDTO(),
                MeritRating = calculationCriteria.MeritRating.ToDTO(),
                ScheduledRatingFactor = calculationCriteria.ScheduledRatingFactor.ToDTO(),
                ShortRatePenaltyFactor = calculationCriteria.ShortRatePenaltyFactor.ToDTO(),
                StandardPremiumDiscounts = calculationCriteria.StandardPremiumDiscounts.ToDTO(),
                ExpenseConstant = calculationCriteria.ExpenseConstant.ToDTO(),
                TerrorismRate = calculationCriteria.TerrorismRate.ToDTO(),
                CatastropheRate = calculationCriteria.CatastropheRate.ToDTO(),
                AuditNonComplianceCharge = calculationCriteria.AuditNonComplianceCharge.ToDTO(),
                BTISServiceFee = calculationCriteria.BTISServiceFee.ToDTO(),
                TotalMinimum = calculationCriteria.TotalMinimum,
                Minimum = calculationCriteria.Minimum,
                Exposure = calculationCriteria.Exposure.ToDTO(),
                IncreasedLimitsForEmployersLiability = calculationCriteria.IncreasedLimitsForEmployersLiability.ToDTO(),
                PolicyAdministrationFees = calculationCriteria.PolicyAdministrationFees.ToDTO(),
                TerritoryFactor = calculationCriteria.TerritoryFactor.ToDTO(),
                DeductibleCredit = calculationCriteria.DeductibleCredit.ToDTO(),
                OutstandingRateIncrease = calculationCriteria.OutstandingRateIncrease.ToDTO(),
                OutstandingRateDecrease = calculationCriteria.OutstandingRateDecrease.ToDTO(),
                CertifiedSafetyCommitteePremiumFactor = calculationCriteria.CertifiedSafetyCommitteePremiumFactor.ToDTO(),
                WorkplaceSafetyCreditFactor = calculationCriteria.WorkplaceSafetyCreditFactor.ToDTO(),
                ConstructionClassification = calculationCriteria.ConstructionClassification.ToDTO(),
                ForeignVoluntaryCoverage = calculationCriteria.ForeignVoluntaryCoverage.ToDTO(),
                AdditionalDefenseCoverage = calculationCriteria.AdditionalDefenseCoverage.ToDTO(),
                IncreasedLimitsForEmployersLiabilityMinimum = calculationCriteria.IncreasedLimitsForEmployersLiabilityMinimum

            };
        }

        public string SplitClassCodeMapping(string State, string ClassCode)
        {
            var splitClassCodeJson = JsonConvert.DeserializeObject<List<SplitClassCodeMapping>>(_configuration["SplitClassCodeMapping"]);
            if (splitClassCodeJson?.Any(a => a.State?.ToLower() == State?.ToLower()) == true)
            {
                string splitClassCode = splitClassCodeJson.Where(s => s.State?.ToLower() == State?.ToLower() && s.ClassCode?.ToLower() == ClassCode?.ToLower()).Select(w => w.TargetClassCode).FirstOrDefault();
                if (!string.IsNullOrEmpty(splitClassCode))
                    return splitClassCode;
            }
            return ClassCode;
        }

        //private List<InvolvedParty> GetInvolvedParty(List<WCLocationClassifications> wcLocationClassifications, DateTime? ProposedEffectiveDate)
        //{
        //    List<InvolvedParty> involvedParties = new();
        //    string branchCode = "250", producerCode = "056835";
        //    var primaryLocationClassfication = wcLocationClassifications.FirstOrDefault(lc => lc.Location.IsPrimary);
        //    if (primaryLocationClassfication != null)
        //    {
        //        List<Domain.Model.CompanyPlacement.Party> parties = new();
        //        decimal permimumRate = 0;
        //        List<PartyQuestionnaire> partyQuestionnaires = new();
        //        Parallel.ForEach(primaryLocationClassfication.Classifications, async (classification, _) =>
        //        {
        //            var classCodes = await _service.SubClassCode(primaryLocationClassfication.Location.State, classification.ClassCode, ProposedEffectiveDate);
        //            parties.Add(new Domain.Model.CompanyPlacement.Party()
        //            {
        //                PrimaryAddress = new Domain.Model.CompanyPlacement.PrimaryAddress() { PostalCode = primaryLocationClassfication.Location.Zip },
        //                OrganizationData = new() { SicCode = classCodes != null ? classCodes?.FirstOrDefault(c => c.DefaultIndicator == 1)?.SIC : "" }
        //            });
        //            var appetite = await _service.GetAppetiteByStateAndClass(primaryLocationClassfication.Location.State, classification.ClassCode, ProposedEffectiveDate);
        //            permimumRate += Convert.ToDecimal((appetite.FirstOrDefault(r => r.CompanyCode == "NF")?.Rate * classification.Payroll)) / 100;
        //            partyQuestionnaires = await GetPartyQuestionnaire(primaryLocationClassfication.Location.State, classification.ClassCode, ProposedEffectiveDate, partyQuestionnaires);
        //        });
        //        partyQuestionnaires.Add(new PartyQuestionnaire()
        //        {
        //            Answer = permimumRate.ToString(),
        //            QuestionId = "ManualPremium",
        //            ExplanationText = "ManualPremium",
        //        });
        //        involvedParties.Add(new InvolvedParty()
        //        {
        //            TypeName = "producingAgency",
        //            PartyLabel = $"{branchCode}{producerCode}",
        //            Party = new()
        //            {
        //                new Domain.Model.CompanyPlacement.Party() { SecondaryProducer = $"{branchCode}{producerCode}" }
        //            }
        //        });
        //        involvedParties.Add(new InvolvedParty()
        //        {
        //            TypeName = "namedInsured",
        //            Party = parties
        //        });

        //        involvedParties.Add(new InvolvedParty()
        //        {
        //            TypeName = "BlazeInputs",
        //            Party = new()
        //            {
        //                new Domain.Model.CompanyPlacement.Party() { PartyRoles = new PartyRoles() { PartyQuestionnaire = partyQuestionnaires } }
        //            }
        //        });
        //        involvedParties.Add(new InvolvedParty()
        //        {
        //            TypeName = "underwritingCompany",
        //            PartyLabel = "NF"
        //        });
        //    }
        //    return involvedParties;
        //}

        //private static List<PolicyQuestionnaire> GetPolicyQuestionnaire(List<CarrierQuestionResponse> questionResponses)
        //{
        //    List<PolicyQuestionnaire> policyQuestionnaires = new();
        //    Parallel.ForEach(questionResponses, (questionResponse, _) =>
        //    {
        //        policyQuestionnaires.Add(new PolicyQuestionnaire()
        //        {
        //            Answer = questionResponse.Answer,
        //            QuestionId = questionResponse.QuestionId,
        //            ExplanationText = questionResponse.Explanation
        //        });


        //    });
        //    ;
        //    return policyQuestionnaires;
        //}

        //private async Task<List<PartyQuestionnaire>> GetPartyQuestionnaire(string State, string ClassCode, DateTime? ProposedEffectiveDate, List<PartyQuestionnaire> partyQuestionnaires)
        //{
        //    var hazardGradeLookup = await _service.HazardGrade(State, ClassCode, ProposedEffectiveDate);
        //    if (hazardGradeLookup != null)
        //    {
        //        partyQuestionnaires.Add(new PartyQuestionnaire()
        //        {
        //            Answer = hazardGradeLookup.Priority,
        //            QuestionId = "SicHazardGrade",
        //            ExplanationText = "SicHazardGrade",
        //        });
        //    }

        //    return partyQuestionnaires;
        //}

        //private static List<RelatedInsurancePolicy> GetWCInsuranceHistory(List<WCInsuranceHistory> wcInsuranceHistories)
        //{
        //    List<RelatedInsurancePolicy> relatedInsurancePolicies = new();
        //    _ = Parallel.ForEach(wcInsuranceHistories, (wcInsuranceHistory, _) =>
        //    {
        //        if (wcInsuranceHistory != null)
        //        {
        //            List<InvolvedParty> involvedParties = new()
        //            {
        //                new InvolvedParty()
        //                {
        //                    TypeName = "numberofTotalClaims",
        //                    PartyLabel = wcInsuranceHistory.NoOfClaims != null ? wcInsuranceHistory.NoOfClaims.ToString() : "0"
        //                },
        //                new InvolvedParty()
        //                {
        //                    TypeName = "numberofMedicalClaims",
        //                    PartyLabel = wcInsuranceHistory?.Claims != null ? wcInsuranceHistory?.Claims.Where(c => c.MedicalOrIndemnity.ToLower() == "medical").Count().ToString() : "0"
        //                },
        //                 new InvolvedParty()
        //                {
        //                    TypeName = "numberofLostTimeClaims",
        //                    PartyLabel = wcInsuranceHistory?.Claims != null ? wcInsuranceHistory?.Claims.Where(c => c.MedicalOrIndemnity.ToLower() == "indemnity").Count().ToString() : "0"
        //                },
        //                  new InvolvedParty()
        //                {
        //                    TypeName = "lossRunDate",
        //                    PartyLabel = ""
        //                }
        //             };
        //            relatedInsurancePolicies.Add(new RelatedInsurancePolicy()
        //            {
        //                InsurancePolicy = new()
        //                {
        //                    PeriodFromDate = wcInsuranceHistory?.StartDate,
        //                    PeriodToDate = wcInsuranceHistory?.EndDate,
        //                    PolicyNumber = wcInsuranceHistory?.PolicyNumber,
        //                    //WorkersCompLine = new()
        //                    //{
        //                    //    WorkersCompRatingStates = GetWorkersCompRatingState(wcInsuranceHistory.LocationsClassifications, wcInsuranceHistory.ProposedEffectiveDate)
        //                    //},
        //                    InvolvedParty = involvedParties
        //                },

        //            });
        //        }
        //    });
        //    return relatedInsurancePolicies;
        //}

        //private List<WorkersCompRatingState> GetWorkersCompRatingState(List<WCLocationClassifications> wcLocationClassifications, DateTime? ProposedEffectiveDate)
        //{
        //    List<WorkersCompRatingState> workersCompRatingStates = new();
        //    Parallel.ForEach(wcLocationClassifications, (wcLocationClassification, _) =>
        //    {
        //        List<WorkersCompClass> workersCompClasses = new();
        //        List<WorkersCompModifier> workersCompModifiers = new();
        //        decimal chargedPremium = 0;
        //        Parallel.ForEach(wcLocationClassification.Classifications, async (classClassifications, _) =>
        //        {
        //            var appetite = await _service.GetAppetiteByStateAndClass(wcLocationClassification.Location.State, classClassifications.ClassCode, ProposedEffectiveDate);
        //            decimal termPremium = Convert.ToDecimal((appetite.FirstOrDefault(r => r.CompanyCode == "NF")?.Rate * classClassifications.Payroll)) / 100;
        //            chargedPremium += termPremium;
        //            workersCompClasses.Add(new WorkersCompClass()
        //            {
        //                TypeName = "S",
        //                ClassCode = classClassifications.ClassCode,
        //                ExposureAmount = classClassifications.Payroll,
        //                Premium = new()
        //                {
        //                    TypeName = "termPremium",
        //                    Amount = Convert.ToDecimal((appetite.FirstOrDefault(r => r.CompanyCode == "NF")?.Rate * classClassifications.Payroll)) / 100
        //                }
        //            });
        //            workersCompModifiers.Add(new WorkersCompModifier()
        //            {
        //                ClassCode = classClassifications.ClassCode,
        //                Premium = new()
        //                {
        //                    TypeName = "termPremium",
        //                    Amount = Convert.ToDecimal((appetite.FirstOrDefault(r => r.CompanyCode == "NF")?.Rate * classClassifications.Payroll)) / 100
        //                },
        //                Rate = new()
        //                {
        //                    TypeName = "modifierRate",
        //                    RatePer = appetite.FirstOrDefault(r => r.CompanyCode == "NF")?.Rate
        //                }
        //            });
        //        });
        //        WorkersCompLocation workersCompLocation = new()
        //        {
        //            LocationNumber = "",
        //            NumberOfEmployees = wcLocationClassification.Classifications.Sum(r => r.NumberOfFullTimeEmployees + r.NumberOfPartTimeEmployees),
        //            PostalAddress = new PostalAddress() { PostalCode = wcLocationClassification.Location.Zip },
        //            WorkersCompClass = workersCompClasses

        //        };
        //        workersCompRatingStates.Add(new WorkersCompRatingState()
        //        {
        //            StateCode = wcLocationClassification.Location.State,
        //            Premium = new()
        //            {
        //                TypeName = "chargedPremium",
        //                Amount = chargedPremium
        //            },
        //            WorkersCompLocation = workersCompLocation,
        //            WorkersCompModifier = workersCompModifiers
        //        });

        //    });
        //    return workersCompRatingStates;
        //}

        public RiskReservationRequest MapRiskReservationRequest(WCSubmissionV2? wcSubmission, ClassCodes? classCode)
        {
            if (wcSubmission is null) return new RiskReservationRequest();
            var primaryLocaion = wcSubmission.LocationsClassifications?.FirstOrDefault(lc => lc.Location.IsPrimary)?.Location;
            var riskReservationRequest = new RiskReservationRequest()
            {
                Party = new()
                {
                    ContactPreference = new() { AddressChangeInd = true },
                    OrganizationData = new()
                    {
                        CompanyData = new()
                        {
                            FeinNumber = wcSubmission.Applicant.FEIN,
                            DunsNumber = "",
                            TunsNumber = "",
                            SicCodeCna = classCode?.SICSequence,
                            SicCodePrimary = classCode?.SIC,
                            SicDescriptionCode = classCode?.SICSequence?.Split("_")?[1],
                        }
                    },
                    PrimaryAddress = new()
                    {
                        AddressLine = primaryLocaion?.Line1,
                        City = primaryLocaion?.City,
                        StateCode = primaryLocaion?.State,
                        PostalCode = primaryLocaion?.Zip,
                        Country = "USA"
                    },
                    PrimaryTelephoneNumber = wcSubmission.Contact.Phones.Count > 1 ? wcSubmission.Contact.Phones.FirstOrDefault(p => p.PhoneType == "primary")?.PhoneNumber : wcSubmission.Contact.Phones.FirstOrDefault()?.PhoneNumber,
                    LegalName = wcSubmission.Applicant != null ? GetCommercialName(wcSubmission.Applicant) : string.Empty,
                    Guid = Guid.NewGuid(),
                   PolicyTerm = new PolicyTerm()
                   {
                       WrittenDate = DateTime.Today.ToString("yyyy-MM-dd"),
                       PolicyTermEffectiveDate = wcSubmission.ProposedEffectiveDate?.ToString("yyyy-MM-dd"),
                       PolicyTermExpirationDate = wcSubmission.ProposedEffectiveDate?.AddYears(1).ToString("yyyy-MM-dd"),
                       QuoteNumber = wcSubmission.SubmissionId,
                       PolicyNumber = ""
                   }
                }
            };
            return riskReservationRequest;
        }

        /// <summary>
        /// This method is used to map company replacement request
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        //public Envelope MapCompanyReplacement(WCSubmission? wcSubmission)
        //{
        //    Envelope envelope = new()
        //    {
        //        Header = new()
        //        {
        //            ServiceConsumer = new()
        //            {
        //                CnaApplicationName = "CNACentralCons",
        //                CnaLanguage = "en",
        //                CnaLocale = "US",
        //                CnaConsumerPlatform = "WAS",
        //                CnaLogLevel = new() { Text = "0" },
        //                CnaCheckPermission = false

        //            },
        //            MessageID = new() { Text = "uuid:b7f9faf7-2a9d-4a1f-83fe-30b485905cbc" },
        //            RelatesTo = new() { Text = "uuid:b7f9faf7-2a9d-4a1f-83fe-30b485905cbc" },
        //            Timestamp = new() { Created = new() { Sender = "vslrch1d234.cna.com", Text = DateTime.UtcNow.ToString() } },
        //            To = new() { Text = "com.cna.app.riskengine.producer.CfRiskEngineProducerImpl" }

        //        },
        //        Body = new()
        //        {
        //            CNAMessage = new()
        //            {
        //                Message = new()
        //                {
        //                    BodyType = "FS-XM",
        //                    CrfCmdMode = "onlyRespondInError",
        //                    Id = "",
        //                    SourceLogicalId = "",
        //                    TimeStampCreated = DateTime.UtcNow.ToString(),
        //                    TxnScope = "all",
        //                    Version = "",
        //                    CrfActionGroup = new()
        //                    {
        //                        BodyCategory = "",
        //                        CrfCmdMode = "onlyRespondInError"
        //                    },
        //                    COMMAND = new()
        //                    {
        //                        CalculateRiskEvaluationRequest = new()
        //                        {
        //                            CmdMode = "onlyRespondInError",
        //                            CmdType = "request",
        //                            InsurancePolicy = new()
        //                            {
        //                                PeriodFromDate = wcSubmission.ProposedEffectiveDate,
        //                                PeriodToDate = wcSubmission.ProposedExpirationDate,
        //                                BoundDate = wcSubmission.ProposedEffectiveDate,
        //                                PolicyQuestionnaire = GetPolicyQuestionnaire(wcSubmission.ClassSpecificResponses),
        //                                RelatedInsurancePolicy = GetWCInsuranceHistory(wcSubmission.InsuranceHistory),
        //                                PrimaryRiskState = wcSubmission.LocationsClassifications.FirstOrDefault(lc => lc.Location.IsPrimary)?.Location.State,
        //                                WorkersCompLine = new()
        //                                {
        //                                    WorkersCompRatingStates = GetWorkersCompRatingState(wcSubmission.LocationsClassifications, wcSubmission.ProposedEffectiveDate)
        //                                },
        //                                InvolvedParty = GetInvolvedParty(wcSubmission.LocationsClassifications, wcSubmission.ProposedEffectiveDate)

        //                            }
        //                        }
        //                    }

        //                }
        //            }
        //        }
        //    };
        //    return envelope;
        //}

        public List<GoverningClassifications> GetGoverningLocation(WCSubmissionV2 wcSubmission, List<WorkersCompRatingState>? workersCompRatingState = null)
        {
            List<GoverningClassifications> governingClassifications = new();
            int locationNumber = 1;
            foreach (var locationsClassifications in wcSubmission.LocationsClassifications)
            {

                var governingClassification = locationsClassifications.Classifications.OrderByDescending(m => m.Payroll).FirstOrDefault();
                if (workersCompRatingState != null)
                {
                    workersCompRatingState.Add(new WorkersCompRatingState()
                    {
                        stateCode = locationsClassifications.Location?.State,
                        workersCompLocation = new WorkersCompLocation()
                        {
                            locationNumber = locationNumber,
                            numberOfEmployees = governingClassification?.NumberOfFullTimeEmployees + governingClassification?.NumberOfPartTimeEmployees,
                            postalCode = locationsClassifications.Location?.Zip,
                            workersCompClass = new WorkersCompClass()
                            {
                                chargedPremium = 0,
                                classCode = governingClassification?.ClassCode,
                                exposureAmount = governingClassification?.Payroll?.ToString()
                            }
                        }
                    });
                }
                foreach (var classification in locationsClassifications.Classifications)
                {
                    if (classification != null)
                    {
                        governingClassifications.Add(new GoverningClassifications
                        {
                            ClassCode = classification.ClassCode,
                            ClassSeq = classification.ClassSeq,
                            StateCode = locationsClassifications?.Location?.State ?? "",
                            Payroll = classification.Payroll,
                            FullTimeEmployee = classification.NumberOfFullTimeEmployees,
                            PartTimeEmployee = classification.NumberOfPartTimeEmployees
                        });
                    }

                }
                locationNumber++;
            }
            return governingClassifications;
        }
        public string GetCommercialName(ApplicantInfo applicantInfo)
        {
            Regex regExpression = new("\\s+");
            string CommercialName = applicantInfo.LegalEntityName != null ? regExpression.Replace(applicantInfo.LegalEntityName.Replace("&", "&amp;"), " ") : "";

            if (applicantInfo.LegalEntity == 7)
            {
                CommercialName = applicantInfo.InsuredFirstName + " " + applicantInfo.InsuredLastName;
            }
            return CommercialName.Length > 60 ? CommercialName[..60] : CommercialName;
        }

        public List<WCLocationClassifications> ProRatePayroll(WCSubmissionV2 model, int cancelledTerm, bool? isFirstProrateCall = false, int? totalDaysUsed = null)
        {
            var fullPolicyTerm = ((model.ProposedExpirationDate.Value.Date - model.ProposedEffectiveDate.Value.Date).Days);
            // fullPolicyTerm = (totalDaysUsed == null || totalDaysUsed == 0) ? fullPolicyTerm : totalDaysUsed.Value;
            var termRate = decimal.Divide(cancelledTerm, fullPolicyTerm);
            for (int i = 0; i < model?.LocationsClassifications?.Count(); i++)
            {
                for (int j = 0; j < model?.LocationsClassifications?[i]?.Classifications?.Count(); j++)
                {
                    var proRatePayroll = termRate * model.LocationsClassifications[i].Classifications[j].Payroll;
                    model.LocationsClassifications[i].Classifications[j].Payroll = isFirstProrateCall == true ? Convert.ToInt32(Math.Floor(proRatePayroll.Value)) : Convert.ToInt32(Math.Ceiling(proRatePayroll.Value));
                }
            }
            return model.LocationsClassifications;
        }
    }
}

