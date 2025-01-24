using BTIS.Contracts.GeneralLiability.Rating.RateLookup;
using BTIS.DotNetLogger.Standard;
using BTIS.Utility.Standard;
using CNA.V2.Domain.DTO;
using CNA.V2.Domain.Model;
using CNA.V2.Domain.Model.CompanyPlacement;
using CNA.V2.Domain.Model.CompanyPlacement.Request;
using CNA.V2.Domain.ResponseModel;
using CNA.V2.Service.Services.Interface;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;


namespace CNA.V2.Service.Services
{
    /// <summary>
    /// Service methods.
    /// </summary>
    public class Service : IService
    {
        #region --Inject dependency in default constructor--
        private readonly ILogger _logger;
        private readonly HttpClient _httpClient;
        private readonly SmartHttpClient _smartHttpClient;
        private readonly ICorrelationIdProvider _correlationIdProvider;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IConfiguration _configuration;

        /// <summary>
        /// Default Constructor
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="correlationIdProvider"></param>
        /// <param name="httpContextAccessor"></param>
        /// <param name="configuration"></param>
        public Service(ILogger<Service> logger, ICorrelationIdProvider correlationIdProvider, IHttpContextAccessor httpContextAccessor, IConfiguration configuration)
        {
            _logger = logger;
            _correlationIdProvider = correlationIdProvider;
            _httpContextAccessor = httpContextAccessor;
            _smartHttpClient = new SmartHttpClient(_correlationIdProvider, _httpContextAccessor);
            _configuration = configuration;
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(300);
        }
        #endregion

        /// <summary>
        /// GetAppetiteByStateAndClass endpoint
        /// </summary>
        /// <param name="state"></param>
        /// <param name="classCode"></param>
        /// <param name="effectiveDate"></param>
        /// <returns></returns>
        public async Task<List<Appetite>?> GetAppetiteByStateAndClass(string state, string classCode, DateTime? effectiveDate)
        {
            string effDate = effectiveDate!.Value.ToString("yyyy-MM-dd");
            //var url = "http://api-azure-test.btisinc.com/WC/v1/gateway/lookup/cna2/appetite/state/classCode?state=DE&classCode=0042&effectiveDate=2023-01-01";
            //var lookupUrl = _configuration["WCGatewayLookup"] + $"cna2/appetite/state/classcode?state={state}&classcode={classCode}&effectiveDate={effDate}";
            var lookupUrl = _configuration["WCGatewayLookup"] + $"cna2/appetite/state/classCode?state={state}&classCode={classCode}&effectiveDate={effDate}";
            _logger.LogInformation($"Calling appetite service endpoint with url: {lookupUrl}");
            var res = await _smartHttpClient.GetAsync(lookupUrl);
            var responseString = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogError($"Error: Response from wc appetite  gateway  :{responseString}");
                return null;
            }
            _logger.LogInformation($"Response from wc appetite gateway endpoint:{responseString}");
            return JsonConvert.DeserializeObject<List<Appetite>>(responseString);

        }

        /// <summary>
        /// GetConfigurationByState endpoint
        /// </summary>
        /// <param name="state"></param>
        /// <param name="effectiveDate"></param>
        /// <returns></returns>
        public async Task<Configuration_Lookup> GetConfigurationByState(string state, DateTime? effectiveDate)
        {
            string effDate = effectiveDate != null ? effectiveDate.Value.ToString("yyyy-MM-dd") : DateTime.Now.ToString("yyyy-MM-dd");
            var lookupUrl = _configuration["WCGatewayLookup"] + $"cna2/configurationLookup/state?state={state}&effectiveDate={effDate}";
            _logger.LogInformation($"Calling config lookup service endpoint with url: {lookupUrl}");
            var res = await _smartHttpClient.GetAsync(lookupUrl);
            var responseString = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogError($"Error: Response from wc gateway lookup configuration_lookup :{responseString}");
                return new Configuration_Lookup();
            }
            _logger.LogInformation($"Response from wc gateway lookup  configuration_lookup endpoint:{responseString}");
            var configuration_Lookup = JsonConvert.DeserializeObject<Configuration_Lookup>(responseString);
            return configuration_Lookup!;
        }

        /// <summary>
        /// GetAppetites endpoint
        /// </summary>
        /// <param name="state"></param>
        /// <param name="classCodes"></param>
        /// <param name="effectiveDate"></param>
        /// <returns></returns>
        public async Task<List<Appetite>?> GetAppetites(string state, string[] classCodes, DateTime? effectiveDate, string companyCode)
        {
            string classCodsString = string.Join(",", classCodes);
            string effDate = effectiveDate!.Value.ToString("yyyy-MM-dd");
            var lookupUrl = _configuration["WCGatewayLookup"] + $"cna2/appetite/state/classCodes/date?state={state}&classCodes={classCodsString}&effectiveDate={effDate}";
            _logger.LogInformation($"Calling appetite service endpoint with url: {lookupUrl}");
            var res = await _smartHttpClient.GetAsync(lookupUrl);
            var responseString = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogError($"Error: Response from wc appetite gateway  :{responseString}");
                return new List<Appetite>();
            }
            _logger.LogInformation($"Response from wc appetite gateway endpoint:{responseString}");
            var appetiteRespose = JsonConvert.DeserializeObject<List<Appetite>>(responseString);
            return appetiteRespose?.Where(r => r.CompanyCode == companyCode).ToList();
        }

        /// <summary>
        /// GetBlanketWaiver
        /// </summary>
        /// <param name="state"></param>
        /// <returns></returns>
        public async Task<List<BlanketWaiver>> GetBlanketWaiver(string state)
        {
            var lookupUrl = _configuration["WCGatewayLookup"] + $"cna2/blanketWaiver/state?state={state}";
            _logger.LogInformation($"Calling BlanketWaiver service endpoint with url: {lookupUrl}");
            var res = await _smartHttpClient.GetAsync(lookupUrl);
            var responseString = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogError($"Error: Response from BlanketWaiver gateway  :{responseString}");
                return new List<BlanketWaiver>();
            }
            _logger.LogInformation($"Response from BlanketWaiver gateway endpoint:{responseString}");
            var blanketWaivers = JsonConvert.DeserializeObject<List<BlanketWaiver>>(responseString);
            return blanketWaivers;
        }

        /// <summary>
        /// GetTeritoryById
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public async Task<Teritory> GetTeritoryById(int id)
        {
            var lookupUrl = _configuration["WCGatewayLookup"] + $"teritorys/territory?territory={id}";
            _logger.LogInformation($"Calling teritory service endpoint with url: {lookupUrl}");
            var res = await _smartHttpClient.GetAsync(lookupUrl);
            var responseString = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogError($"Error: Response from teritory gateway  :{responseString}");
                return new Teritory();
            }
            _logger.LogInformation($"Response from teritory gateway endpoint:{responseString}");
            var teritory = JsonConvert.DeserializeObject<Teritory>(responseString);
            return teritory;
        }

        /// <summary>
        /// GetRatingFormula
        /// </summary>
        /// <param name="state"></param>
        /// <returns></returns>
        public async Task<Formula> GetRatingFormula(string state, DateTime effectiveDate)
        {
            var lookupUrl = _configuration["WCGatewayLookup"] + $"cna2/ratingFormula/state?state={state}&effectiveDate={effectiveDate}";
            _logger.LogInformation($"Calling RatingFormula service endpoint with url: {lookupUrl}");
            var res = await _smartHttpClient.GetAsync(lookupUrl);
            var responseString = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogError($"Error: Response from RatingFormula gateway  :{responseString}");
                return new Formula();
            }
            _logger.LogInformation($"Response from RatingFormula gateway endpoint:{responseString}");

            var ratingFormula = JsonConvert.DeserializeObject<Formula>(responseString);
            return ratingFormula;
        }

        /// <summary>
        /// GetPolicyFeeByState
        /// </summary>
        /// <param name="state"></param>
        /// <returns></returns>
        public async Task<List<PolicyFee>> GetPolicyFeeByState(string state)
        {
            var lookupUrl = _configuration["WCGatewayLookup"] + $"cna2/policyFee/state?state={state}";
            _logger.LogInformation($"Calling policy fee service endpoint with url: {lookupUrl}");
            var res = await _smartHttpClient.GetAsync(lookupUrl);
            var responseString = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogError($"Error: Response from  policy fee  gateway  :{responseString}");
                return new List<PolicyFee>();
            }
            _logger.LogInformation($"Response from policy fee gateway endpoint:{responseString}");
            return JsonConvert.DeserializeObject<List<PolicyFee>>(responseString);
        }

        /// <summary>
        /// GetPremiumDiscountByState
        /// </summary>
        /// <param name="state"></param>
        /// <returns></returns>
        public async Task<List<Premium_Discount>?> GetPremiumDiscountByState(string state, string? companycode)
        {
            //   var lookupUrl = _configuration["WCGatewayLookup"] + $"cna2/premiumDiscountByState?state={state}";
            var lookupUrlByStateAndCompanyCode = _configuration["WCGatewayLookup"] + $"cna2/companyBasicType?state={state}&companyCode={companycode}";
            _logger.LogInformation($"Calling premium discount service endpoint with url: {lookupUrlByStateAndCompanyCode}");
            var resByStateAndCompanyCode = await _smartHttpClient.GetAsync(lookupUrlByStateAndCompanyCode);
            var responseStringByStateAndCompanyCode = await resByStateAndCompanyCode.Content.ReadAsStringAsync();
            if (!resByStateAndCompanyCode.IsSuccessStatusCode)
            {
                _logger.LogError($"Error: Response from  premium discount  gateway  :{responseStringByStateAndCompanyCode}");
                return new List<Premium_Discount>();
            }
            _logger.LogInformation($"Response from premium discount gateway endpoint:{responseStringByStateAndCompanyCode}");
            var campanyBasicType = JsonConvert.DeserializeObject<List<Domain.Model.CompanyBasisType>>(responseStringByStateAndCompanyCode);
            if (campanyBasicType == null || !campanyBasicType.Any())
            {
                _logger.LogError($"Error: No Response from  company basic type  :{responseStringByStateAndCompanyCode}");
                return new List<Premium_Discount>();
            }
            List<Premium_Discount> resultList = new List<Premium_Discount>();
            foreach (var company in campanyBasicType)
            {
                var lookupUrl = _configuration["WCGatewayLookup"] + $"cna2/premiumDiscountByState?state={state}&companyBasisType={company.CompanyBasicType}";
                _logger.LogInformation($"Calling premium discount service endpoint with url: {lookupUrl}");
                var res = await _smartHttpClient.GetAsync(lookupUrl);
                var responseString = await res.Content.ReadAsStringAsync();
                if (!res.IsSuccessStatusCode)
                {
                    _logger.LogError($"Error: Response from  premium discount  gateway  :{responseString}");
                    //return new List<Premium_Discount>();
                }
                else
                {
                    _logger.LogInformation($"Response from premium discount gateway endpoint:{responseString}");

                    var premiumDiscount = JsonConvert.DeserializeObject<List<Premium_Discount>>(responseString);
                    if (premiumDiscount != null && premiumDiscount.Any() )
                        resultList.AddRange(premiumDiscount);
                }
            }

            return resultList;
            //if (companycode == "CC" || companycode == "TP")
            //    return premiumDiscount?.Where(x => x.CompanyBasisType == CompanyBasisType.NONSTOCK.ToString()).ToList();
            //else
            //    return premiumDiscount?.Where(x => x.CompanyBasisType == CompanyBasisType.STOCK.ToString()).ToList();
        }

        /// <summary>
        /// GetTaxesByState
        /// </summary>
        /// <param name="state"></param>
        /// <param name="effectiveDate"></param>
        /// <returns></returns>
        public async Task<List<Taxes>?> GetTaxesByState(string state, DateTime effectiveDate)
        {
            string effDate = effectiveDate.ToString("yyyy-MM-dd");
            var lookupUrl = _configuration["WCGatewayLookup"] + $"cna2/taxes/state?state={state}&effectiveDate={effDate}";
            _logger.LogInformation($"Calling taxes service endpoint with url: {lookupUrl}");
            var res = await _smartHttpClient.GetAsync(lookupUrl);
            var responseString = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogError($"Error: Response from taxes gateway  :{responseString}");
                return new List<Taxes>();
            }
            _logger.LogInformation($"Response from taxes gateway endpoint:{responseString}");
            var taxes = JsonConvert.DeserializeObject<List<Taxes>>(responseString);
            return taxes;
        }

        /// <summary>
        /// GetLimitsByStateAndId
        /// </summary>
        /// <param name="state"></param>
        /// <param name="limitId"></param>
        /// <returns></returns>
        public async Task<Limits?> GetLimitsByStateAndId(string state, int limitId)
        {
            var lookupUrl = _configuration["WCGatewayLookup"] + $"cna2/limit/limitID?limitID={limitId}&State={state}";
            _logger.LogInformation($"Calling limits service endpoint with url: {lookupUrl}");
            var res = await _smartHttpClient.GetAsync(lookupUrl);
            var responseString = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogError($"Error: Response from limits gateway  :{responseString}");
                return new Limits();
            }
            _logger.LogInformation($"Response from limits gateway endpoint:{responseString}");
            var limits = JsonConvert.DeserializeObject<Limits>(responseString);
            return limits;
        }

        /// <summary>
        /// LineItemBreakdown
        /// </summary>
        /// <param name="state"></param>
        /// <returns></returns>
        public async Task<HttpResponseMessage> LineItemBreakdown(string state)
        {
            var lookupUrl = _configuration["WCGatewayLookup"] + $"cna2/configPremiumBreakdown/state?state={state}";
            _logger.LogInformation($"Calling Config Premium Breakdown service endpoint with url: {lookupUrl}");
            var res = await _smartHttpClient.GetAsync(lookupUrl);
            //var responseString = await res.Content.ReadAsStringAsync();
            //if (!res.IsSuccessStatusCode)
            //{
            //    _logger.LogError($"Error: Response from BlanketWaiver gateway  :{responseString}");
            //    return new HttpResponseMessage ();
            //}
            //_logger.LogInformation($"Response from Config Premium Breakdown gateway endpoint:{responseString}");
            //var blanketWaivers = JsonConvert.DeserializeObject<URLResponse>(responseString);
            return res;
        }

        /// <summary>
        /// Service method to call CNA endpoint.
        /// </summary>
        /// <param name="state"></param>
        /// <param name="classCode"></param>
        /// <param name="effectiveDate"></param>
        /// <returns></returns>
        public async Task<List<ClassCodes>?> GetSubClassCode(string? state, string? classCode, DateTime effectiveDate)
        {
            string effDate = effectiveDate.ToString("yyyy-MM-dd");
            var url = _configuration["WCGatewayLookup"] + $"cna2/SICmappingbydate?State={state}&ClassCode={classCode}&effectiveDate={effDate}";
            _logger.LogDebug($"Calling CNA SICmapping endpoint with request URL:{url}");
            var res = await _smartHttpClient.GetAsync(url);
            var responseString = await res?.Content?.ReadAsStringAsync();
            _logger.LogDebug($"Response from CNA WCGatewayLookup endpoint:{responseString}");
            try
            {
                if (res.IsSuccessStatusCode)
                {
                    var response = JsonConvert.DeserializeObject<List<ClassCodes>>(responseString);
                    return response;
                }
                else
                {
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"CNA get SICmapping getting failed. Exception Message:{ex.Message}, StackTrace:{ex.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// HazardGrade
        /// </summary>
        /// <param name="state"></param>
        /// <param name="classCode"></param>
        /// <param name="effectiveDate"></param>
        /// <returns></returns>
        public async Task<HazardGradeLookup?> HazardGrade(string state, string classCode, DateTime effectiveDate)
        {
            string effDate = effectiveDate.ToString("yyyy-MM-dd");
            var url = _configuration["WCGatewayLookup"] + $"cna2/hazardgradebydate?State={state}&ClassCode={classCode}&effectiveDate={effDate}";
            _logger.LogDebug($"Calling CNA hazardgradebydate endpoint with request URL:{url}");
            var res = await _smartHttpClient.GetAsync(url);
            var responseString = await res?.Content?.ReadAsStringAsync();
            _logger.LogDebug($"Response from CNA hazardgradebydate endpoint:{responseString}");
            try
            {
                if (res.IsSuccessStatusCode)
                {
                    var response = JsonConvert.DeserializeObject<HazardGradeLookup>(responseString);
                    return response;
                }
                else
                {
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"CNA get hazardgradebydate getting failed. Exception Message:{ex.Message}, StackTrace:{ex.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// TerrorismByStateAndClassCode
        /// </summary>
        /// <param name="state"></param>
        /// <param name="classCode"></param>
        /// <param name="effectiveDate"></param>
        /// <returns></returns>
        public async Task<Terrorism?> TerrorismByStateAndCompanyCode(string state, string companyCode, DateTime effectiveDate)
        {
            string effDate = effectiveDate.ToString("yyyy-MM-dd");
            var url = _configuration["WCGatewayLookup"] + $"cna2/terrorismbydate?State={state}&CompanyCode={companyCode}&effectiveDate={effDate}";
            _logger.LogDebug($"Calling CNA TerrorismByStateandCompanyCode endpoint with request URL:{url}");
            var res = await _smartHttpClient.GetAsync(url);
            var responseString = await res?.Content?.ReadAsStringAsync()!;
            _logger.LogDebug($"Response from CNA TerrorismByStateandCompanyCode endpoint:{responseString}");
            try
            {
                if (res.IsSuccessStatusCode)
                {
                    var response = JsonConvert.DeserializeObject<Terrorism>(responseString);
                    return response;
                }
                else
                {
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"CNA get TerrorismByStateandCompanyCode getting failed. Exception Message:{ex.Message}, StackTrace:{ex.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// CatastropheByStateAndClassCode
        /// </summary>
        /// <param name="state"></param>
        /// <param name="classCode"></param>
        /// <param name="effectiveDate"></param>
        /// <returns></returns>
        public async Task<Catastrophe?> CatastropheByStateAndCompanyCode(string state, string companyCode, DateTime effectiveDate)
        {
            string effDate = effectiveDate.ToString("yyyy-MM-dd");
            var url = _configuration["WCGatewayLookup"] + $"cna2/catastrophebydate?State={state}&CompanyCode={companyCode}&effectiveDate={effDate}";
            _logger.LogDebug($"Calling CNA CatastropheByStateAndCompanyCode endpoint with request URL:{url}");
            var res = await _smartHttpClient.GetAsync(url);
            var responseString = await res?.Content?.ReadAsStringAsync()!;
            _logger.LogDebug($"Response from CNA CatastropheByStateAndCompanyCode endpoint:{responseString}");
            try
            {
                if (res.IsSuccessStatusCode)
                {
                    var response = JsonConvert.DeserializeObject<Catastrophe>(responseString);
                    return response;
                }
                else
                {
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"CNA get CatastropheByStateAndCompanyCode getting failed. Exception Message:{ex.Message}, StackTrace:{ex.StackTrace}");
                return null;
            }
        }

        public async Task<WcSubmission?> GetWcSubmission(string submissionID)
        {
            var url = $"{_configuration["BaseURL"]}WC/v1/wcsubmission/Submission/{submissionID}";
            //var localURL = "http://localhost:49531/Submission/CWC00183600";
            _logger.LogDebug($"Calling Submission GET endpoint to get the submission info with url: {url}");
            var res = await _smartHttpClient.GetAsync(url);
            _logger.LogDebug($"Response from Submission GET method. Response Status:{res.StatusCode}");
            string resString = await res.Content.ReadAsStringAsync();

            var resObject = JsonConvert.DeserializeObject<ResponseViewModel<WcSubmission>>(resString);
            return resObject?.Response;
        }

        public async Task<ShortRatePenalties?> GetShortRatePenalties(int? noOfDays)
        {
            var lookupUrl = _configuration["WCGatewayLookup"] + $"cna2/ShortRatePenaltiesByDaysInPolicy?DaysInPolicy={noOfDays}";
            _logger.LogInformation($"Calling shortRatePenalties service endpoint with url: {lookupUrl}");
            var res = await _smartHttpClient.GetAsync(lookupUrl);
            var responseString = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogError($"Error: Response from shortRatePenalties gateway  :{responseString}");
                return new ShortRatePenalties();
            }
            _logger.LogInformation($"Response from shortRatePenalties gateway endpoint:{responseString}");
            var shortRatePenalties = JsonConvert.DeserializeObject<ShortRatePenalties>(responseString);
            return shortRatePenalties;
        }

        public async Task<List<NonRateble>?> NonRatablesByStateAndCompanyCode(string state, string classCode, string companyCode, DateTime? effectiveDate)
        {
            string effDate = effectiveDate!.Value.ToString("yyyy-MM-dd");
            //"http://api-azure-test.btisinc.com/WC/v1/gateway/lookup/cna2/nonRatables/state?State=DE&ClassCode=0512&CompanyCode=CIC";
            var url = _configuration["WCGatewayLookup"] + $"cna2/nonRatables/state?State={state}&ClassCode={classCode}&CompanyCode={companyCode}"; //&effectiveDate={effDate}
            _logger.LogDebug($"Calling CNA NonRatablesByStateAndCompanyCode endpoint with request URL:{url}");
            var res = await _smartHttpClient.GetAsync(url);
            var responseString = await res?.Content?.ReadAsStringAsync()!;
            _logger.LogDebug($"Response from CNA NonRatablesByStateAndCompanyCode endpoint: {responseString}");
            try
            {
                if (res.IsSuccessStatusCode)
                {
                    return JsonConvert.DeserializeObject<List<NonRateble>>(responseString);
                }
                else
                {
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"CNA get NonRatablesByStateAndCompanyCode getting failed. Exception Message:{ex.Message}, StackTrace:{ex.StackTrace}");
                return null;
            }
        }

        public async Task<CompanyBasisType?> GetDefaultCompany(string? state)
        {
            //http://api-azure-test.btisinc.com/WC/v1/gateway/lookup/cna2/companyCodeByState?state=CA
            var url = _configuration["WCGatewayLookup"] + $"cna2/companyCodeByState?state={state}";
            _logger.LogInformation($"Calling CNA CompanyCodeByState lookup with request URL:{url}");
            var res = await _smartHttpClient.GetAsync(url);
            var responseString = await res?.Content?.ReadAsStringAsync()!;
            _logger.LogDebug($"Response from CNA CompanyCodeByState endpoint: {responseString}");
            try
            {
                if (res.IsSuccessStatusCode)
                {
                    var companyBasisTypes =  JsonConvert.DeserializeObject<List<CompanyBasisType>>(responseString);
                    return companyBasisTypes?.FirstOrDefault(c => c.CompanyBase == 1);
                }
                else
                {
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"CNA CompanyCodeByState getting failed. Exception Message:{ex.Message}, StackTrace:{ex.StackTrace}");
                return null;
            }

        }

        public async Task<bool> ComapnyDeviationCondition(string? state,string? classcode, DateTime? proposedEffectiveDate, int? totalEmployees)
        {
            var companyPlacementSICList = await GetCompanyPlacementSIC();
            var classCodeList = await GetSubClassCode(state, classcode, proposedEffectiveDate??DateTime.Now);
            //Company Placement should be applied for all the SIC(158) attached in the sheet for the states not in CA, FL, MA, ME, NJ, TX, and WI
            var classCode = classCodeList?.OrderByDescending(x => x.StartDate).FirstOrDefault(x => x.DefaultIndicator == 1) ?? classCodeList?.FirstOrDefault();
            var companyPlacementSIC = companyPlacementSICList.Where(s => s.SIC == classCode?.SICSequence).FirstOrDefault();
            // Company Placement should be applied for all the SIC code other than the(SIC)158 mentioned(Step3), if 25 or more Employees, for the states not in CA, FL, MA, ME, NJ, TX, and WI
            if (companyPlacementSIC == null && totalEmployees < 25)
            {
                return false;
            }
            return true;
        }

        public async Task<List<CompanyPlacementSIC>> GetCompanyPlacementSIC()
        {
            var lookupUrl = _configuration["WCGatewayLookup"] + $"cna/placement/SIC";
            var res = await _smartHttpClient.GetAsync(lookupUrl);
            var responseString = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogError("Error: Response from wc appetite gateway.LookupUrl: {URL}, StatusCode: {ResponseStatus}, Response: {Body}", lookupUrl, res.StatusCode, responseString);
                return new List<CompanyPlacementSIC>();
            }
            else
            {
                _logger.LogInformation("Success: Response from wc appetite gateway endpoint. LookupUrl: {URL}, StatusCode: {ResponseStatus}, Response: {Body}", lookupUrl, res.StatusCode, responseString);
                return JsonConvert.DeserializeObject<List<CompanyPlacementSIC>>(responseString);
            }

        }

    }
}
