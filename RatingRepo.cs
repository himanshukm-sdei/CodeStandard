using BTIS.Utility.Standard;
using CNA.V2.Domain.Model;
using CNA.V2.Repository.Interface;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace CNA.V2.Repository.Implementation
{
    /// <summary>
    /// Appetite Repository
    /// </summary>
    public class RatingRepo<TEntity> : IRatingRepo<TEntity> where TEntity : class
    {
        private readonly ILogger _logger;
        private readonly IMongoDatabase database;
        private readonly IConfiguration _configuration;
        private static readonly IMongoClient client = new MongoClient(GeneralPurpose.GetConsulKeyAsync("Services/WorkersComp/MongoDBUri").Result);

        /// <summary>
        /// Default Constructor
        /// </summary>
        public RatingRepo(ILogger<RatingRepo<TEntity>> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            database = client.GetDatabase(_configuration["RatingDatabase"]);
        }

        /// <summary>
        /// This method is used to insert rating details DOM.
        /// </summary>
        /// <param name="wc_rating"></param>
        /// <returns></returns>
        public async Task<TEntity> Create(TEntity wc_rating)
        {
            await database.GetCollection<TEntity>(typeof(TEntity).Name).InsertOneAsync(wc_rating).ConfigureAwait(false);
            return wc_rating;
        }

        /// <summary>
        /// This method is used to get rating details.
        /// </summary>
        /// <param name="submissionId"></param>
        /// <param name="source"></param>
        /// <returns>wc_ratings</returns>
        public async Task<WC_ratings> GetRating(string submissionId, string source)
        {
            var collection = await database.GetCollection<WC_ratings>(typeof(WC_ratings).Name)?.Find(x => x.SubmissionId == submissionId && x.Source == source)?.FirstOrDefaultAsync();
            return collection;
        }

        /// <summary>
        /// This method is used to update rating details.
        /// </summary>
        /// <param name="wcrating"></param>
        /// <returns>wc_ratings</returns>
        public async Task<WC_ratings> UpdateRating(WC_ratings wcrating)
        {
            var filter = Builders<WC_ratings>.Filter.Eq(x => x._id, wcrating._id) & Builders<WC_ratings>.Filter.Eq(x => x.SubmissionId, wcrating.SubmissionId);
            await database.GetCollection<WC_ratings>(typeof(WC_ratings).Name).ReplaceOneAsync(filter, wcrating).ConfigureAwait(false);
            return wcrating;
        }
    }
}
