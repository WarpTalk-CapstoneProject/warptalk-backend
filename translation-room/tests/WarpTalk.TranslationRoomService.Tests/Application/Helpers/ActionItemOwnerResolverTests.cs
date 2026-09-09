using System;
using System.Collections.Generic;
using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Domain.Entities;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Helpers;

/// <summary>
/// Matching the name a meeting said to the person who was in it.
///
/// The two failures are not symmetrical, and every case here is written from that. An unresolved
/// owner is a line that reads "Nhi" and cannot be assigned — mildly annoying, visibly incomplete.
/// A wrong resolution is a task sitting in a colleague's list, with the meeting's authority behind
/// it, that they never agreed to. So the resolver refuses on ambiguity, and these tests are mostly
/// about the refusals.
/// </summary>
public class ActionItemOwnerResolverTests
{
    private static TranslationRoomParticipant Person(string displayName) => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = displayName,
        Status = "CONNECTED",
        Role = "PARTICIPANT",
        SpeakLanguage = "vi",
        ListenLanguage = "vi"
    };

    [Fact]
    public void APersonCalledByOnePartOfTheirNameIsFound()
    {
        var tu = Person("Huỳnh Thái Tú");
        var people = new List<TranslationRoomParticipant> { tu, Person("Ngô Xuân Hạnh Nhi") };

        ActionItemOwnerResolver.Resolve("Tú", people).Should().Be(tu);
    }

    [Fact]
    public void AnAddressTermInFrontOfTheNameIsIgnored()
    {
        var tu = Person("Huỳnh Thái Tú");

        ActionItemOwnerResolver.Resolve("anh Tú", new List<TranslationRoomParticipant> { tu })
            .Should().Be(tu);
        ActionItemOwnerResolver.Resolve("chị Tú", new List<TranslationRoomParticipant> { tu })
            .Should().Be(tu);
    }

    [Fact]
    public void TheFullNameAlsoMatchesARosterHoldingOnlyAShortOne()
    {
        // Containment works both ways: a model that helpfully wrote the whole name must still
        // find a participant who joined as "Tú".
        var tu = Person("Tú");

        ActionItemOwnerResolver.Resolve("Huỳnh Thái Tú", new List<TranslationRoomParticipant> { tu })
            .Should().Be(tu);
    }

    [Fact]
    public void MatchingIgnoresCaseAndPunctuation()
    {
        var tu = Person("Huỳnh Thái Tú");

        ActionItemOwnerResolver.Resolve("  tú.  ", new List<TranslationRoomParticipant> { tu })
            .Should().Be(tu);
    }

    [Fact]
    public void ANameTwoPeopleCouldAnswerToResolvesToNobody()
    {
        // This is the case a guess gets wrong, and the one that puts work in the wrong list.
        var people = new List<TranslationRoomParticipant>
        {
            Person("Ngô Xuân Hạnh Nhi"),
            Person("Trần Nhi")
        };

        ActionItemOwnerResolver.Resolve("Nhi", people).Should().BeNull();
    }

    [Fact]
    public void DiacriticsAreNotFoldedAway()
    {
        // In Vietnamese they are the difference between words, not decoration. "Tu" and "Tú" are
        // different people, and a resolver that folded them would assign across that line.
        var tu = Person("Huỳnh Thái Tú");

        ActionItemOwnerResolver.Resolve("Tu", new List<TranslationRoomParticipant> { tu })
            .Should().BeNull();
    }

    [Fact]
    public void SomebodyWhoWasNotInTheMeetingResolvesToNobody()
    {
        var people = new List<TranslationRoomParticipant> { Person("Huỳnh Thái Tú") };

        ActionItemOwnerResolver.Resolve("Ngọc Vân", people).Should().BeNull();
    }

    [Fact]
    public void AnEmptyOwnerResolvesToNobodyRatherThanToTheFirstPerson()
    {
        var people = new List<TranslationRoomParticipant> { Person("Huỳnh Thái Tú") };

        ActionItemOwnerResolver.Resolve(null, people).Should().BeNull();
        ActionItemOwnerResolver.Resolve("", people).Should().BeNull();
        ActionItemOwnerResolver.Resolve("   ", people).Should().BeNull();
    }

    [Fact]
    public void AnOwnerThatIsOnlyAnAddressTermIdentifiesNobody()
    {
        // Dropping every word would otherwise leave an empty set, and an empty set is contained
        // in every name — which would match the first person tried.
        var people = new List<TranslationRoomParticipant> { Person("Huỳnh Thái Tú") };

        ActionItemOwnerResolver.Resolve("anh", people).Should().BeNull();
        ActionItemOwnerResolver.Resolve("mr", people).Should().BeNull();
    }

    [Fact]
    public void AnEmptyRoomResolvesToNobody()
    {
        ActionItemOwnerResolver.Resolve("Tú", new List<TranslationRoomParticipant>()).Should().BeNull();
    }

    [Fact]
    public void AParticipantWithNoDisplayNameIsNeverMatched()
    {
        var nameless = Person("");
        var tu = Person("Huỳnh Thái Tú");

        ActionItemOwnerResolver.Resolve("Tú", new List<TranslationRoomParticipant> { nameless, tu })
            .Should().Be(tu);
    }
}
