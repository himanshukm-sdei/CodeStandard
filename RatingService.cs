using BTIS.Contracts.GeneralLiability.Rating.RateLookup;
using BTIS.Contracts.Rating;
using BTIS.GeneralLiability;
using BTIS.Linq;
using BTIS.Rating.Calculation;
using CNA.V2.Domain.DTO;
using CNA.V2.Domain.Model;
using CNA.V2.Domain.ResponseModel;
using CNA.V2.Repository.Interface;
using CNA.V2.Service.Helper;
using CNA.V2.Service.Services.Interface;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Text;
using CalculationCriteria = CNA.V2.Domain.Model.CalculationCriteria;

namespace CNA.V2.Service.Services
{
    public class RatingService : IRatingService
    {
        readonly ILogger _logger;
        readonly IRatingRepo<WC_ratings> _ratingRepo;
        private readonly IService _service;
        private readonly IHelperMethod _helperMethod;

        public RatingService(ILogger<RatingService> logger, IRatingRepo<WC_ratings> ratingRepo, IService service, IHelperMethod helperMethod)
        {
            _logger = logger;
            _ratingRepo = ratingRepo;
            _service = service;
            _helperMethod = helperMethod;
        }

        public CalculationCriteria BuildCalculationCriteria(CalculationData calculationData)
        {
            try
            {
                CalculationCriteria calculationCriteria = new()
                {
                    Payrolls = new Factor<string>("Payrolls", calculationData?.ClassificationPayrolls?.Select(r => new FactorPart<string>(r.Key, new RateTier(r.Value)))),
                    BaseRates = new Factor<string>("BaseRate", calculationData?.GetClassCodeBaseRates.Select(r => new FactorPart<string>(r.Key, new RateTier(r.Value)))),
                    IncreasedLimitsForEmployersLiabilityFactor = new Factor("IncreasedLimitsForEmployersLiabilityFactor", new RateTier(calculationData?.IncreasedLimitsForEmployersLiability.Factor ?? 1)),
                    TerritoryFactor = new Factor("TerritoryFactor", new RateTier(calculationData?.GetTerritoryFactor ?? 1)),
                    IncreasedLimitsForEmployersLiabilityMinimum = calculationData?.IncreasedLimitsForEmployersLiability.Minimum,
                    BlanketWaiverOfSubrogation = new Charges(new List<Charge>() { new("BlanketWaiverOfSubrogation", 1, new ChargeTier(0M, 0M, (calculationData.BlanketWaiverOfSubrogation.Factor, 0)), calculationData.BlanketWaiverOfSubrogation.Minimum) }),
                    SpecificWaiver = new Factor("SpecificWaiver", new RateTier(calculationData.SpecificWaiver ?? 1)),
                    Xmod = new Factor("Xmod", new RateTier(calculationData?.XMod ?? 1)),
                    MeritRating = new Factor("MeritRating", new RateTier(calculationData?.MeritRating??1)),
                    ScheduledRatingFactor = new Factor("ScheduledRatingFactor", new RateTier((decimal)(calculationData?.ScheduledRatingFactor ?? 1))),
                    ShortRatePenaltyFactor = new Factor("ShortRatePenaltyFactor", new RateTier(calculationData?.ShortRatePenaltyFactor ?? 1)),
                    TotalMinimum = calculationData?.TotalMinimum ?? 0,
                    StandardPremiumDiscounts = new Charges(new List<Charge>() { new("StandardPremiumDiscounts", 1, calculationData.StandardPremiumDiscounts.Select(f => new ChargeTier(f.Min, 0, technicalRate: (f.Value, 0))), isGraduated: true) }),
                    ExpenseConstant = new Factor("ExpenseConstant", new RateTier(calculationData.ExpenseConstant)),
                    TerrorismRate = new Factor("TerrorismRate", new RateTier(calculationData.TerrorismRate)),
                    CatastropheRate = new Factor("CatastropheRate", new RateTier(calculationData.CatastropheRate)),
                    AuditNonComplianceCharge = new Factor("AuditNonComplianceCharge", new RateTier(calculationData.AuditNonComplianceCharge)),
                    BTISServiceFee = new Charges(new List<Charge>() { new("BTISServiceFee", 1, calculationData.BTISServiceFee.Select(f => new ChargeTier(f.Min, f.Value))) }),
                    IncreasedLimitsForEmployersLiability = new Charges(new List<Charge>() { new("IncreasedLimitsForEmployersLiability", 1, new ChargeTier(0M, 0M, (calculationData.IncreasedLimitsForEmployersLiability.Factor, 0)), calculationData.IncreasedLimitsForEmployersLiability.Minimum) }),
                    DeductibleCredit = new Factor("DeductibleCredit", new RateTier(calculationData.DeductibleCredit ?? 1)),
                    Exposure = new Exposure(value: 0),
                    Minimum = calculationData.Minimum != null ? calculationData.Minimum.Value : 0,
                    OutstandingRateIncrease = new Factor("OutstandingRateIncrease", new RateTier(calculationData.OutstandingRateIncrease ?? 0)),
                    OutstandingRateDecrease = new Factor("OutstandingRateDecrease", new RateTier(calculationData.OutstandingRateDecrease ?? 0)),
                    WorkplaceSafetyCreditFactor = new Factor("WorkplaceSafetyCreditFactor", new RateTier(calculationData.WorkplaceSafetyCreditFactor ?? 1)),
                    ConstructionClassification = new Factor("ConstructionClassification", new RateTier(calculationData.ConstructionClassification ?? 1)),
                    CertifiedSafetyCommitteePremiumFactor = new Factor("CertifiedSafetyCommitteePremiumFactor", new RateTier(calculationData.CertifiedSafetyCommitteePremiumFactor ?? 1)),
                    ForeignVoluntaryCoverage = new Factor("ForeignVoluntaryCoverage", new RateTier(calculationData.CertifiedSafetyCommitteePremiumFactor ?? 1)),
                    AdditionalDefenseCoverage = new Factor("AdditionalDefenseCoverage", new RateTier(calculationData.CertifiedSafetyCommitteePremiumFactor ?? 0)),
                };
                return calculationCriteria;
            }
            catch (Exception ex)
            {
                _logger.LogError("Error occured in BuildCalculationCriteria {Message} {StackTrace}", ex.Message, ex.StackTrace);
                return new CalculationCriteria();
            }
        }

        public async Task<CalculationReportData> BuildCalculationReportData(RatingCriteria criteria, string? companyPlacementCode, Configuration_Lookup? configuration_Lookup, int? noOfDays = 0)
        {
            CalculationReportData calculationReportData = new();
            List<Appetite> appetites = new();

            for (int i = 0; i < criteria?.ClassificationPayrolls?.Count; i++)
            {
                var item = criteria.ClassificationPayrolls.ElementAt(i);
                string itemKey = item.Key;
                string itemValue = item.Value.ToString();

                // Getting class code factors.
                var res = await _service.GetAppetiteByStateAndClass(criteria?.State?.ToString(), itemKey, criteria?.EffectiveDate);
                var appetiteBasedOnCompanyCode = res?.FirstOrDefault(x => x.CompanyCode == companyPlacementCode);
                if (appetiteBasedOnCompanyCode != null)
                {
                    appetites.Add(appetiteBasedOnCompanyCode);
                }
            }

            calculationReportData.Appetites = appetites;

            // Getting configuration lookup data,
            //var factorsTask = Task.Run(() => _service.GetConfigurationByState(criteria?.State, criteria.EffectiveDate));

            // Getting policy fee data for given state.
            var policyFeesTask = Task.Run(() => _service.GetPolicyFeeByState(criteria?.State));

            // Getting discounts line items for given state.
            var premiumDiscountsTask = Task.Run(() => _service.GetPremiumDiscountByState(criteria?.State, companyPlacementCode));

            // Getting territory for zip code.
            // Calling Helper method to get the first three digits.
            var terrId = HelperMethod.TakeNDigits(criteria?.ZipCode ?? 0, 3);
            var territoryTask = Task.Run(() => _service.GetTeritoryById(terrId));

            // Getting limits for given limit id.
            var limitsTask = Task.Run(() => _service.GetLimitsByStateAndId(criteria?.State, criteria?.LimitId ?? 0));

            //Get Rates
            var terrrorismTask = Task.Run(() => _service.TerrorismByStateAndCompanyCode(criteria?.State, companyPlacementCode, criteria.EffectiveDate));
            var catastropherTask = Task.Run(() => _service.CatastropheByStateAndCompanyCode(criteria?.State, companyPlacementCode, criteria.EffectiveDate));

            //Get ShortRatePenalties
            var ShortRatePenaltiesTask = Task.Run(() => _service.GetShortRatePenalties(noOfDays));

            // Running all methods in parallel.
            await Task.WhenAll(policyFeesTask, premiumDiscountsTask, territoryTask, limitsTask, terrrorismTask, catastropherTask, ShortRatePenaltiesTask);

            // Getting GetBlanketWaiver for given state.
            var blanketWaivers = await _service.GetBlanketWaiver(criteria?.State);
            var blanketWaiver = blanketWaivers.Where(x => x.Type == "Blanket").FirstOrDefault();

            calculationReportData.Configuration_Lookup = configuration_Lookup;
            calculationReportData.PolicyFees = await policyFeesTask;
            calculationReportData.Premium_Discounts = await premiumDiscountsTask;
            calculationReportData.Teritory = await territoryTask;
            calculationReportData.Limit = await limitsTask;
            calculationReportData.BlanketWaiver = blanketWaiver;
            calculationReportData.TerrorismFactor = (await terrrorismTask)?.TerrorismFactor ?? 0;
            calculationReportData.CatastropheFactor = (await catastropherTask)?.CatastropheFactor ?? 0;
            calculationReportData.ShortRatePenalties = (await ShortRatePenaltiesTask);
            return calculationReportData;
        }

        public async Task<RatingWorksheet> GetRatingDetail(Formula formula, CalculationCriteria calculationCriteria, RatingCriteria ratingCriteria)
        {
            try
            {

                var builder = new BTIS.Rating.RatingCalculationBuilder(new BTIS.Rating.DefaultOperatorExpressionFactory(), typeof(CalculationCriteria), new Type[] { typeof(string), typeof(Hazards) });

                foreach (Instruction instruction in formula.Instructions)
                {
                    try
                    {
                        builder.AppendOperation(instruction.Operation, instruction.Operands);

                    }
                    catch (Exception ex)
                    {

                        throw;
                    }

                }
                var ethingy = builder.ToExpression();
                var cthingy = ethingy.Compile();

                var calculator = (formula: formula, rater: cthingy as Func<CalculationCriteria, IRatingResult<string, Hazards>>);
                BTIS.Rating.DTO.RatingResult<string, Hazards> result = calculator.rater(calculationCriteria).ToDTO();

                List<Taxes> taxes = new();
                var taxeCollection = await _service.GetTaxesByState(ratingCriteria?.State?.ToString(), ratingCriteria.EffectiveDate);
                if (taxeCollection != null)
                {
                    taxes = taxeCollection.GroupBy(x => x.ItemName).Select(g => g.OrderByDescending(x => x.StartDate).First()).ToList();
                }
                return new RatingWorksheet()
                {
                    Formula = new BTIS.Rating.Formula(calculator.formula.Instructions.Select(i => new BTIS.Rating.Instruction(i.Operation, i.Operands.ToArray()))).ToDTO(),
                    Rates = HelperMethod.MapCalculationCriteriaDTO(calculationCriteria),
                    Rating = result,
                    Criteria = ratingCriteria,
                    Taxes = taxes
                };
            }
            catch (Exception ex)
            {
                _logger.LogError("Error occured in GetRatingDetail {Message} {StackTrace}", ex.Message,ex.StackTrace);
                throw;
            }

        }

        public async Task<WC_ratings> SaveRatingData(RatingCriteria criteria, RatingWorksheet ratingWorksheet, string? companyPlacementCode, decimal gradientTargetPremium)
        {
            _logger.LogDebug($"get rating worksheet from mongo collection using SubmisionId: {criteria?.SubmissionId}");
            var ratingResult = await _ratingRepo.GetRating(criteria?.SubmissionId?.ToString(), criteria?.Source?.ToString());
            WC_ratings wcratings = new()
            {
                RatingWorksheet = ratingWorksheet,
                CompanyPlacementCode = companyPlacementCode,
                GradientTargetPremium = gradientTargetPremium
            };
            if (ratingResult != null)
            {
                _logger.LogDebug($"Update rating worksheet in mongo and get rating detail Id: {JsonConvert.SerializeObject(ratingWorksheet)}");
                wcratings.SubmissionId = ratingResult.SubmissionId;
                wcratings.RatingDetailsId = ratingResult.RatingDetailsId;
                wcratings.ProgramName = ratingResult.ProgramName;
                  wcratings.Source = ratingResult.Source?.ToString();
                wcratings._id = ratingResult._id;
                wcratings.LastUpdated = DateTime.Now;
                wcratings.CreateDate = ratingResult.CreateDate;
                return await _ratingRepo.UpdateRating(wcratings);
            }
            else
            {
                _logger.LogDebug($"Save rating worksheet in mongo and get rating detail Id: {JsonConvert.SerializeObject(ratingWorksheet)}");
                 wcratings.CreateDate = DateTime.Now;
                wcratings.SubmissionId = criteria.SubmissionId;
                wcratings.RatingDetailsId = criteria.RatingDetailsId;
                wcratings.ProgramName = criteria.ProgramName;
                wcratings.Source = criteria.Source?.ToString();
                return await _ratingRepo.Create(wcratings);
            }

        }

        public async Task<bool> SaveRatingSchMod(string submissionId, string source)
        {
            _logger.LogDebug("Get rating worksheet from mongo collection using SubmisionId: {submissionId}",submissionId);
            var ratingResult = await _ratingRepo.GetRating(submissionId, source);
            if (ratingResult != null && ratingResult.RatingWorksheet?.Rates!=null )
            {
                _logger.LogDebug("Update ScheduledRatingFactor in rating worksheet: {ratingResult}", JsonConvert.SerializeObject(ratingResult));
                ratingResult.RatingWorksheet.Criteria.ScheduledRatingFactor = 1.0M;
                ratingResult.RatingWorksheet.Rates.ScheduledRatingFactor = new Factor("ScheduledRatingFactor", new RateTier((decimal)(1))).ToDTO();
                ratingResult.LastUpdated = DateTime.Now;
                await _ratingRepo.UpdateRating(ratingResult);
                return true;
            }
            return false;
        }

        public async Task<RatingWorksheet> GetNetRate(RatingWorksheet ratingWorksheet, CalculationCriteria calculationCriteria)
        {
            var ratesObj = ratingWorksheet.Rates;
            var baseRates = ratesObj.BaseRates.Items.Select(a => new { a.Classification, a.Tiers[0].Rate });

            // This logic needs to be changed to get the base rates also on the basis of premium breakdown.
            //if (ratingWorksheet.Criteria.State == "CA")
            //{
            baseRates = ratingWorksheet.Rating.Phases[2].ClassificationFactor.Items.Select(a => new { a.Classification, a.Tiers[0].Rate });
            //}
            var bwos = ratesObj.BlanketWaiverOfSubrogation.Items[0].Tiers[0].TechnicalFactor;
            var el = ratesObj.IncreasedLimitsForEmployersLiability.Items[0].Tiers[0].TechnicalFactor;
            var exmod = ratesObj.Xmod.Tiers[0].Rate;
            var schMod = ratesObj.ScheduledRatingFactor.Tiers[0].Rate;
            //var alcoholAndDrugFree = ratesObj.AlcoholDrugFreeWorkplace.Tiers[0].Rate;
            var meritRatingFactor = ratesObj.MeritRating.Tiers[0].Rate;
            //var OGSERPSupplementalFactor = ratesObj.OGSERPSupplemental.Tiers[0].Rate;
            //var contractingClassPremAdjProgramFactor = ratesObj.ContractingClassPremAdjProgram.Tiers[0].Rate;
            //var heathcareNetworkCreditfactor = ratesObj.CertifiedWCHeathcareNtwrkCredit.Tiers[0].Rate;
            //var certifiedRiskMgtProgramOrServiceFactor = ratesObj.CertifiedRiskMgtProgram.Tiers[0].Rate;
            var terrorismFactor = ratesObj.TerrorismRate.Tiers[0].Rate;
            var catastropheFactor = ratesObj.CatastropheRate.Tiers[0].Rate;
            var deductible_Amt = ratesObj.DeductibleCredit.Tiers[0].Rate;

            var result = await _service.LineItemBreakdown(ratingWorksheet.Criteria.State);
            var resString = await result?.Content?.ReadAsStringAsync();
            string responseString = "{ Response : " + resString.ToString() + "}";

            var apiResponseJson = JsonConvert.DeserializeObject<URLResponse>(responseString);
            string premiumDiscountAmountPath = "";
            string standardPremiumPath = "";
            string preBeforeDeductiblePath = "";
            foreach (var JsonValue in apiResponseJson.Response)
            {
                if (JsonValue.Key == "premiumDiscount")
                {
                    premiumDiscountAmountPath = JsonValue.Value.ToString();
                    StringBuilder sb = new StringBuilder(premiumDiscountAmountPath);
                    premiumDiscountAmountPath = sb.Remove(0, 16).ToString();
                }
                if (JsonValue.Key == "standardPremium")
                {
                    standardPremiumPath = JsonValue.Value.ToString();
                    StringBuilder sb = new StringBuilder(standardPremiumPath);
                    standardPremiumPath = sb.Remove(0, 16).ToString();
                }
                if (ratingWorksheet.Criteria.State == "PA" && JsonValue.Key == "premiumAfterEmployersLiability")
                {
                    preBeforeDeductiblePath = JsonValue.Value.ToString();
                    StringBuilder sb = new StringBuilder(preBeforeDeductiblePath);
                    preBeforeDeductiblePath = sb.Remove(0, 16).ToString();

                }
                if (ratingWorksheet.Criteria.State == "DE" && JsonValue.Key == "premiumAfterConstructionClassification")
                {
                    preBeforeDeductiblePath = JsonValue.Value.ToString();
                    StringBuilder sb = new StringBuilder(preBeforeDeductiblePath);
                    preBeforeDeductiblePath = sb.Remove(0, 16).ToString();
                }
            }

            var serializeRatingWC = JsonConvert.SerializeObject(ratingWorksheet);
            var jObj = JObject.Parse(serializeRatingWC);

            var premiumDiscountAmount = jObj.SelectToken(premiumDiscountAmountPath)?.ToString();
            var standardPremium = jObj.SelectToken(standardPremiumPath)?.ToString();
            var premiumDiscount = Convert.ToDecimal(premiumDiscountAmount) > 0 ?
                (Convert.ToDecimal(standardPremium) > 0 ?
                (Convert.ToDecimal(premiumDiscountAmount) / Convert.ToDecimal(standardPremium)) : 0) : 0;

            var premBeforeDeductible = jObj.SelectToken(preBeforeDeductiblePath)?.ToString();
            var deductible_Percentage = (deductible_Amt > 0 && Convert.ToDecimal(premBeforeDeductible) != 0) ? Convert.ToDecimal(deductible_Amt) / Convert.ToDecimal(premBeforeDeductible) : 0;

            calculationCriteria.NetRates = new Factor<string>(
                    "NetRates",
                     baseRates.Select(r => new FactorPart<string>(
                r.Classification,
                new RateTier(
                    Math.Round(
                        (Convert.ToDecimal(r.Rate) *
                        (1 + Convert.ToDecimal(bwos)) *
                        (1 + Convert.ToDecimal(el)) *
                        Convert.ToDecimal(exmod) *
                        Convert.ToDecimal(schMod) *
                        (1 - Convert.ToDecimal(premiumDiscount)) *
                        (1 - Convert.ToDecimal(deductible_Percentage)) *
                        //Convert.ToDecimal(alcoholAndDrugFree) *
                        Convert.ToDecimal(meritRatingFactor)
                        //Convert.ToDecimal(OGSERPSupplementalFactor) *
                        //Convert.ToDecimal(contractingClassPremAdjProgramFactor) *
                        //Convert.ToDecimal(heathcareNetworkCreditfactor) *
                        //Convert.ToDecimal(certifiedRiskMgtProgramOrServiceFactor)
                        ) +
                        Convert.ToDecimal(terrorismFactor) +
                        Convert.ToDecimal(catastropheFactor)
                        , 2)
                        )))
                );

            ratingWorksheet.Rates.NetRates = calculationCriteria.NetRates.ToDTO();

            return ratingWorksheet;
        }

        public async Task<ResponseViewModel<WCResponse>> GetRatingResponse(WCSubmissionV2 wcSubmissionModel, WCLocationClassifications? primaryLocation, Configuration_Lookup config_look, string companyPlacementCode, decimal? minPremium, decimal? expenseConstant, RatingCriteria? objCalculationRequest, decimal? schModifier = null, bool quickAction = false, int? noOfDays = null)
        {
            objCalculationRequest.SubmissionId = string.IsNullOrEmpty(objCalculationRequest.SubmissionId) ? Guid.NewGuid().ToString("N") : objCalculationRequest.SubmissionId;
            objCalculationRequest.RatingDetailsId = Guid.NewGuid().ToString("N");
            objCalculationRequest.ScheduledRatingFactor = schModifier == null || schModifier == 0 ? 1 : schModifier.Value <= config_look.MaxSchForUW ? schModifier.Value : config_look.MaxSchForUW;

            decimal gradientTargetPremium = Convert.ToDecimal(wcSubmissionModel.KeyValues?.Where(x => x.Key == "GradientTargetPremium").Select(y => y.Value).FirstOrDefault() ?? "0");


            var originalRatingWorksheet = await GetRatingDetails(objCalculationRequest, expenseConstant, companyPlacementCode, config_look, noOfDays);
            var originalPremium = HelperMethod.GetOriginalPremium(originalRatingWorksheet, minPremium, originalRatingWorksheet?.Rating?.Phases?.Reverse());// Convert.ToInt32(originalRatingDetails.RatingWorksheet?.Rating?.Phases?.Reverse()?.Skip(2)?.FirstOrDefault()?.Premium?.Value);

            var ratingDetails = new WC_ratings();
            var schModratingWorksheet = originalRatingWorksheet;
            if (originalPremium > minPremium && gradientTargetPremium != 0)
            {
                ratingDetails = await SaveRatingSheet(objCalculationRequest, companyPlacementCode, originalRatingWorksheet, gradientTargetPremium);
                var governingClassification = primaryLocation?.Classifications.OrderByDescending(m => m.Payroll).FirstOrDefault();
                var subClassCodes = await _service.GetSubClassCode(primaryLocation?.Location.State, governingClassification?.ClassCode, wcSubmissionModel.ProposedEffectiveDate ?? DateTime.Now);
                var sicCode = subClassCodes?.Select(s => s.SIC).ToArray();
                var companyPlacementSICList = await _service.GetCompanyPlacementSIC();
                //Company Placement should be applied for all the SIC(158) attached in the sheet for the states not in CA, FL, MA, ME, NJ, TX, and WI
                var companyPlacementSIC = companyPlacementSICList.Where(s => sicCode.Contains(s.SIC)).FirstOrDefault();
                var totalEmployees = primaryLocation?.Classifications.Sum(c => c.NumberOfFullTimeEmployees) + primaryLocation?.Classifications.Sum(c => c.NumberOfPartTimeEmployees);

                if (companyPlacementSIC != null || totalEmployees > 24)
                {
                    //Apply company deviation if  premium is greater than min after schedule rating
                    string[] companies = { "AC", "NF", "CI", "VF", "CC" };
                    Dictionary<string, CompanyRating> companiesPremium = new()
                    {
                        [companyPlacementCode] = new CompanyRating() { WCRating = ratingDetails, Premium = originalPremium, SubmissionId = objCalculationRequest.SubmissionId }
                    };
                    foreach (var company in companies)
                    {
                        if (company == companyPlacementCode) continue;
                        objCalculationRequest.SubmissionId = Guid.NewGuid().ToString("N");
                        var companyRatingWorksheet = await GetRatingDetails(objCalculationRequest, expenseConstant, company, config_look, noOfDays);
                        var companyRatingDetails = await SaveRatingSheet(objCalculationRequest, company, companyRatingWorksheet, gradientTargetPremium);
                        var companyPremium = HelperMethod.GetOriginalPremium(companyRatingWorksheet, minPremium, companyRatingWorksheet?.Rating?.Phases?.Reverse());// Convert.ToInt32(originalRatingDetails.RatingWorksheet?.Rating?.Phases?.Reverse()?.Skip(2)?.FirstOrDefault()?.Premium?.Value);
                        companiesPremium[company] = new CompanyRating { WCRating = companyRatingDetails, Premium = companyPremium, SubmissionId = objCalculationRequest.SubmissionId };
                    }
                    var nearest = companiesPremium.OrderBy(x => Math.Abs(x.Value.Premium - gradientTargetPremium)).First();
                    originalPremium = nearest.Value.Premium;
                    ratingDetails = nearest.Value.WCRating;
                    companyPlacementCode = nearest.Key;
                    objCalculationRequest.SubmissionId = nearest.Value.SubmissionId;
                    originalRatingWorksheet = nearest.Value.WCRating.RatingWorksheet;
                }
                else
                {
                    _logger.LogInformation("Employees are less than 25 and not in the company placement SIC list, LegalEntityName: {LegalEntityName}, NumberOfFullTimeEmployees: {NumberOfFullTimeEmployees}, NumberOfPartTimeEmployees: {NumberOfPartTimeEmployees}", wcSubmissionModel.Applicant.LegalEntityName, primaryLocation?.Classifications.Sum(c => c.NumberOfFullTimeEmployees), primaryLocation?.Classifications.Sum(c => c.NumberOfPartTimeEmployees));

                }
            }
            int claimCount = wcSubmissionModel?.InsuranceHistory != null && wcSubmissionModel?.InsuranceHistory?.Count > 0 ? wcSubmissionModel.InsuranceHistory.Select(x => x.NoOfClaims.Value).Sum() : 0;
            int lapsesCoverage = (string.IsNullOrEmpty(wcSubmissionModel?.Applicant?.NoOfPriorCoverage) && Convert.ToInt32(wcSubmissionModel?.Applicant?.NoOfPriorCoverage) > 0) ? Convert.ToInt32(wcSubmissionModel?.Applicant?.NoOfPriorCoverage) : 0;
            if (originalPremium > minPremium && (schModifier == null || schModifier == 0) && (claimCount == 0 && lapsesCoverage == 0))
            {
                if (quickAction)
                    objCalculationRequest.ScheduledRatingFactor = config_look.QuickActionSchModifier;
                else
                {
                    _logger.LogInformation($"Calculate ScheduledModifier from Rate POST endpoint : SubmissionId {objCalculationRequest?.SubmissionId?.ToString()}");
                    objCalculationRequest.ScheduledRatingFactor = _helperMethod.CalculateScheduleModifier(wcSubmissionModel?.CreditResponses, config_look.MaxSchForAgent);

                }
                if (objCalculationRequest.ScheduledRatingFactor != 1)
                {
                    _logger.LogInformation($"WCResponse mapping from Rate POST endpoint : SubmissionId {objCalculationRequest?.SubmissionId?.ToString()}");
                    schModratingWorksheet = await GetRatingDetails(objCalculationRequest, expenseConstant, companyPlacementCode, config_look, noOfDays);
                    var afterSchModPremium = HelperMethod.GetOriginalPremium(schModratingWorksheet, minPremium, schModratingWorksheet?.Rating?.Phases?.Reverse());// Convert.ToInt32(originalRatingDetails.RatingWorksheet?.Rating?.Phases?.Reverse()?.Skip(2)?.FirstOrDefault()?.Premium?.Value);
                    if (originalPremium != afterSchModPremium)
                    {
                        ratingDetails = await SaveRatingSheet(objCalculationRequest, companyPlacementCode, schModratingWorksheet, gradientTargetPremium);
                    }
                    else
                    {
                        objCalculationRequest.ScheduledRatingFactor = 1;
                        ratingDetails = await SaveRatingSheet(objCalculationRequest, companyPlacementCode, originalRatingWorksheet, gradientTargetPremium);
                    }
                }
                else
                {
                    ratingDetails = await SaveRatingSheet(objCalculationRequest, companyPlacementCode, originalRatingWorksheet, gradientTargetPremium);
                }

            }
            else
            {
                ratingDetails = await SaveRatingSheet(objCalculationRequest, companyPlacementCode, originalRatingWorksheet, gradientTargetPremium);
            }
            WCResponse wcResponse = _helperMethod.WCResponseMapping(primaryLocation, originalRatingWorksheet, schModratingWorksheet, wcSubmissionModel.ProposedEffectiveDate.Value, minPremium, objCalculationRequest.ScheduledRatingFactor, companyPlacementCode, ratingDetails.SubmissionId, ratingDetails.RatingDetailsId);
            wcResponse = await _helperMethod.GetGoverningClassCode(primaryLocation.Location.State, ratingDetails, wcResponse);
            var response = HelperMethod.ResponseMapping<WCResponse>((int)HttpStatusCode.OK, "Quote created Successfully.", wcResponse, null);
            return response;
        }

        /// <summary>
        /// GetRatingDetails
        /// </summary>
        /// <param name="criteria"></param>
        /// <param name="expenseConstant"></param>
        /// <param name="companyPlacementCode"></param>
        /// <param name="configuration_Lookup"></param>
        /// <returns></returns>
        public async Task<RatingWorksheet> GetRatingDetails(RatingCriteria criteria, decimal? expenseConstant, string? companyPlacementCode, Configuration_Lookup? configuration_Lookup, int? noOfDays = 0)
        {
            _ = new ResponseViewModel<WC_ratings>();
            _logger.LogInformation("Rating Service - Build calculation data");
            CalculationReportData calculationReportData = await BuildCalculationReportData(criteria, companyPlacementCode, configuration_Lookup, noOfDays);

            _logger.LogInformation("Helper Method - Map calculation report data model to calculation data model");
            var calculationData = _helperMethod.MapCalculationData(calculationReportData, criteria, expenseConstant);

            _logger.LogInformation("Rating Service - Build calculation criteria");
            CalculationCriteria calculationCriteria = BuildCalculationCriteria(calculationData);

            Formula ratingFormula = await _service.GetRatingFormula(criteria.State,criteria.EffectiveDate);

            _logger.LogInformation("Rating Service - Build calculation criteria");
            RatingWorksheet ratingWorksheet = await GetRatingDetail(ratingFormula, calculationCriteria, criteria);

            _logger.LogInformation("Calculation of Net Rate");
            return await GetNetRate(ratingWorksheet, calculationCriteria);

        }

        public async Task<WC_ratings> SaveRatingSheet(RatingCriteria criteria, string? companyPlacementCode, RatingWorksheet ratingWorksheet, decimal gradientTargetPremium = 0)
        {
            _logger.LogInformation("Rating Service - Saving rating data.");
            return await SaveRatingData(criteria, ratingWorksheet, companyPlacementCode, gradientTargetPremium);
        }

    }
}
