// ---------------------------------------------------------------------------------------------------
// <auto-generated>
// This code was generated by LinqToDB scaffolding tool (https://github.com/linq2db/linq2db).
// Changes to this file may cause incorrect behavior and will be lost if the code is regenerated.
// </auto-generated>
// ---------------------------------------------------------------------------------------------------

using LinqToDB.Mapping;

#pragma warning disable 1573, 1591
#nullable enable

namespace RaterBot.Database
{
	[Table("TopPostsDay")]
	public class TopPostsDay
	{
		[Column("Id"    , IsPrimaryKey = true, IsIdentity = true, SkipOnInsert = true, SkipOnUpdate = true)] public long Id     { get; set; } // integer
		[Column("ChatId"                                                                                  )] public long ChatId { get; set; } // integer
		[Column("PostId"                                                                                  )] public long PostId { get; set; } // integer
	}
}