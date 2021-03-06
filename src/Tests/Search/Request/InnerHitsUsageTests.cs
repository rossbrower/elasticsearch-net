﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Bogus;
using Elasticsearch.Net;
using FluentAssertions;
using Nest;
using Tests.Framework;
using Tests.Framework.Integration;
using Tests.Framework.MockData;
using Xunit;
using static Nest.Infer;

namespace Tests.Search.Request
{
	public interface IRoyal
	{
		string Name { get; set; }
	}

	[ElasticsearchType(IdProperty = "Name")]
	public abstract class RoyalBase<TRoyal> : IRoyal
		where TRoyal : class, IRoyal
	{
		protected static int IdState = 0;
		public string Name { get; set; }
		public static Faker<TRoyal> Generator { get; } =
			new Faker<TRoyal>()
				.RuleFor(p => p.Name, f => f.Person.Company.Name + IdState++);
	}

	public class King : RoyalBase<King>
	{
		public List<King> Foes { get; set; }
	}
	public class Prince : RoyalBase<Prince> { }
	public class Duke : RoyalBase<Duke> { }
	public class Earl : RoyalBase<Earl> { }
	public class Baron : RoyalBase<Baron> { }

	public class RoyalSeeder
	{
		private readonly IElasticClient _client;
		private readonly IndexName _index;

		public RoyalSeeder(IElasticClient client, IndexName index) { this._client = client; this._index = index; }

		public void Seed()
		{
			var create = this._client.CreateIndex(this._index, c => c
				.Settings(s => s
					.NumberOfReplicas(0)
					.NumberOfShards(1)
				)
				.Mappings(map => map
					.Map<King>(m => m.AutoMap()
						.Properties(props =>
							RoyalProps(props)
							.Nested<King>(n => n.Name(p => p.Foes).AutoMap())
						)
					)
					.Map<Prince>(m => m.AutoMap().Properties(RoyalProps).Parent<King>())
					.Map<Duke>(m => m.AutoMap().Properties(RoyalProps).Parent<Prince>())
					.Map<Earl>(m => m.AutoMap().Properties(RoyalProps).Parent<Duke>())
					.Map<Baron>(m => m.AutoMap().Properties(RoyalProps).Parent<Earl>())
				 )
			);

			var kings = King.Generator.Generate(2)
				.Select(k =>
				{
					k.Foes = King.Generator.Generate(2).ToList();
					return k;
				});

			var bulk = new BulkDescriptor();
			IndexAll(bulk, () => kings, indexChildren: king =>
				 IndexAll(bulk, () => Prince.Generator.Generate(2), king.Name, prince =>
					 IndexAll(bulk, () => Duke.Generator.Generate(3), prince.Name, duke =>
						 IndexAll(bulk, () => Earl.Generator.Generate(5), duke.Name, earl =>
							 IndexAll(bulk, () => Baron.Generator.Generate(1), earl.Name)
						 )
					 )
				 )
			);
			this._client.Bulk(bulk);
			this._client.Refresh(this._index);
		}

		private PropertiesDescriptor<TRoyal> RoyalProps<TRoyal>(PropertiesDescriptor<TRoyal> props) where TRoyal : class, IRoyal =>
			props.String(s => s.Name(p => p.Name).NotAnalyzed());

		private void IndexAll<TRoyal>(BulkDescriptor bulk, Func<IEnumerable<TRoyal>> create, string parent = null, Action<TRoyal> indexChildren = null)
			where TRoyal : class, IRoyal
		{
			var current = create();
			//looping twice horrible but easy to debug :)
			var royals = current.ToList();
			foreach (var royal in royals)
			{
				var royal1 = royal;
				bulk.Index<TRoyal>(i => i.Document(royal1).Index(this._index).Parent(parent));
			}
			if (indexChildren == null) return;
			foreach (var royal in royals)
				indexChildren(royal);
		}
	}

	[Collection(IntegrationContext.OwnIndex)]
	public abstract class InnerHitsApiTestsBase<TRoyal> : ApiIntegrationTestBase<ISearchResponse<TRoyal>, ISearchRequest, SearchDescriptor<TRoyal>, SearchRequest<TRoyal>>
		where TRoyal : class, IRoyal
	{
		public InnerHitsApiTestsBase(OwnIndexCluster cluster, EndpointUsage usage) : base(cluster, usage) { }

		protected abstract IndexName Index { get; }
		protected override void BeforeAllCalls(IElasticClient client, IDictionary<ClientMethod, string> values) => new RoyalSeeder(this.Client, Index).Seed();

		protected override ConnectionSettings GetConnectionSettings(ConnectionSettings settings) => settings
			.DisableDirectStreaming();

		protected override LazyResponses ClientUsage() => Calls(
			fluent: (client, f) => client.Search<TRoyal>(f),
			fluentAsync: (client, f) => client.SearchAsync<TRoyal>(f),
			request: (client, r) => client.Search<TRoyal>(r),
			requestAsync: (client, r) => client.SearchAsync<TRoyal>(r)
		);

		protected override bool ExpectIsValid => true;
		protected override int ExpectStatusCode => 200;
		protected override HttpMethod HttpMethod => HttpMethod.POST;
		protected override string UrlPath => $"/{Index}/{this.Client.Infer.TypeName<TRoyal>()}/_search";

		protected override bool SupportsDeserialization => true;

		protected override SearchDescriptor<TRoyal> NewDescriptor() => new SearchDescriptor<TRoyal>().Index(Index);
	}

	[Collection(IntegrationContext.OwnIndex)]
	public class GlobalInnerHitsApiTests : InnerHitsApiTestsBase<Duke>
	{
		public GlobalInnerHitsApiTests(OwnIndexCluster cluster, EndpointUsage usage) : base(cluster, usage) { }

		private static IndexName IndexName { get; } = RandomString();
		protected override IndexName Index => GlobalInnerHitsApiTests.IndexName;

		protected override object ExpectJson { get; } = new
		{
			inner_hits = new
			{
				earls = new
				{
					type = new
					{
						earl = new
						{
							fielddata_fields = new[] { "name" },
							inner_hits = new
							{
								barons = new { type = new { baron = new { } } }
							},
							size = 5
						}
					}
				}
			}
		};

		protected override Func<SearchDescriptor<Duke>, ISearchRequest> Fluent => s => s
			.Index(Index)
			.InnerHits(ih => ih
				.Type<Earl>("earls", g => g
					.Size(5)
					.InnerHits(iih => iih
						.Type<Baron>("barons")
					)
					.FielddataFields(p => p.Name)
				)
			);

		protected override SearchRequest<Duke> Initializer => new SearchRequest<Duke>(Index, typeof(Duke))
		{
			InnerHits = new NamedInnerHits
			{
				{ "earls", new InnerHitsContainer
				{
					Type = new TypeInnerHit<Earl>
					{
						InnerHit = new GlobalInnerHit
						{
							Size = 5,
							FielddataFields = new Field[]{ "name" },
							InnerHits = new NamedInnerHits
							{
								{ "barons", new TypeInnerHit<Baron>() }
							}
						}
					}
				} }
			}
		};

		[I] public Task AssertResponse() => this.AssertOnAllResponses(r =>
		{
			r.IsValid.Should().BeTrue();
			r.Hits.Should().NotBeEmpty();
			foreach (var hit in r.Hits)
			{
				hit.InnerHits.Should().NotBeEmpty();
				hit.InnerHits.Should().ContainKey("earls");
				var earlHits = hit.InnerHits["earls"].Hits;
				earlHits.Total.Should().BeGreaterThan(0);
				earlHits.Hits.Should().NotBeEmpty().And.HaveCount(5);
				foreach (var earlHit in earlHits.Hits)
					earlHit.Fields.ValuesOf<string>("name").Should().NotBeEmpty();
				var earls = earlHits.Documents<Earl>();
				earls.Should().NotBeEmpty().And.OnlyContain(earl => !string.IsNullOrWhiteSpace(earl.Name));
				foreach (var earlHit in earlHits.Hits)
				{
					var earl = earlHit.Source.As<Earl>().Name;
					var baronHits = earlHit.InnerHits["barons"];
					baronHits.Should().NotBeNull();
					var baron = baronHits.Documents<Baron>().FirstOrDefault();
					baron.Should().NotBeNull();
					baron.Name.Should().NotBeNullOrWhiteSpace();
				}
			}
		});
	}

	[Collection(IntegrationContext.OwnIndex)]
	public class QueryInnerHitsApiTests : InnerHitsApiTestsBase<King>
	{
		public QueryInnerHitsApiTests(OwnIndexCluster cluster, EndpointUsage usage) : base(cluster, usage) { }

		private static IndexName IndexName { get; } = RandomString();
		protected override IndexName Index => QueryInnerHitsApiTests.IndexName;

		protected override object ExpectJson { get; } = new
		{
			query = new
			{
				@bool = new
				{
					should = new object[] {
					new {
						has_child = new {
							type = "prince",
							query = new { match_all = new {} },
							inner_hits = new { name = "princes" }
						}
					},
					new {
						nested = new {
							query = new { match_all = new {} },
							path = "foes",
							inner_hits = new {}
						}
					}
				}
				}
			}
		};

		protected override Func<SearchDescriptor<King>, ISearchRequest> Fluent => s => s
			.Index(Index)
			.Query(q =>
				q.HasChild<Prince>(hc => hc
					.Query(hcq => hcq.MatchAll())
					.InnerHits(ih => ih.Name("princes"))
				) || q.Nested(n => n
					.Path(p => p.Foes)
					.Query(nq => nq.MatchAll())
					.InnerHits()
				)
			);

		protected override SearchRequest<King> Initializer => new SearchRequest<King>(Index, typeof(King))
		{
			Query = new HasChildQuery
			{
				Type = typeof(Prince),
				Query = new MatchAllQuery(),
				InnerHits = new InnerHits { Name = "princes" }
			} || new NestedQuery
			{
				Path = Field<King>(p => p.Foes),
				Query = new MatchAllQuery(),
				InnerHits = new InnerHits()
			}
		};

		[I] public Task AssertResponse() => this.AssertOnAllResponses(r =>
		{
			r.Hits.Should().NotBeEmpty();
			foreach (var hit in r.Hits)
			{
				var princes = hit.InnerHits["princes"].Documents<Prince>();
				princes.Should().NotBeEmpty();

				var foes = hit.InnerHits["foes"].Documents<King>();
				foes.Should().NotBeEmpty();
			};
		});
	}

}
