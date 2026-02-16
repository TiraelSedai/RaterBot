using Shouldly;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using RaterBot;

namespace RaterBot.Tests.Unit;

public class TelegramHelperTests
{
    [Fact]
    public void LinkToMessage_Supergroup_ReturnsCorrectLink()
    {
        var chat = new Chat { Id = -1001234567890, Type = ChatType.Supergroup };
        var result = TelegramHelper.LinkToMessage(chat, 42);
        result.ShouldBe("https://t.me/c/1234567890/42");
    }

    [Fact]
    public void LinkToMessage_GroupWithUsername_ReturnsCorrectLink()
    {
        var chat = new Chat { Id = -1001234567890, Type = ChatType.Group, Username = "testgroup" };
        var result = TelegramHelper.LinkToMessage(chat, 42);
        result.ShouldBe("https://t.me/testgroup/42");
    }

    [Fact]
    public void LinkToMessage_GroupWithoutUsername_ReturnsEmpty()
    {
        var chat = new Chat { Id = -1001234567890, Type = ChatType.Group, Username = null };
        var result = TelegramHelper.LinkToMessage(chat, 42);
        result.ShouldBeEmpty();
    }

    [Fact]
    public void GetFirstLastName_WithBothNames_ReturnsCombined()
    {
        var user = new User { Id = 1, FirstName = "John", LastName = "Doe" };
        var result = TelegramHelper.GetFirstLastName(user);
        result.ShouldBe("John Doe");
    }

    [Fact]
    public void GetFirstLastName_OnlyFirstName_ReturnsIt()
    {
        var user = new User { Id = 1, FirstName = "John", LastName = null };
        var result = TelegramHelper.GetFirstLastName(user);
        result.ShouldBe("John");
    }

    [Fact]
    public void GetFirstLastName_EmptyNames_ReturnsAnonymous()
    {
        var user = new User { Id = 1, FirstName = "", LastName = null };
        var result = TelegramHelper.GetFirstLastName(user);
        result.ShouldBe("аноним");
    }

    [Fact]
    public void GetFirstLastName_WhitespaceNames_ReturnsAnonymous()
    {
        var user = new User { Id = 1, FirstName = "   ", LastName = " " };
        var result = TelegramHelper.GetFirstLastName(user);
        result.ShouldBe("аноним");
    }

    [Fact]
    public void UserEscaped_EscapesSpecialCharacters()
    {
        var user = new User { Id = 1, FirstName = "Test_User", LastName = "[Bot]" };
        var result = TelegramHelper.UserEscaped(user);
        result.ShouldContain("\\_");
        result.ShouldContain("\\[");
        result.ShouldContain("\\]");
    }

    [Fact]
    public void UserEscaped_EscapesAllSpecialChars()
    {
        var user = new User { Id = 1, FirstName = "T*e_s`t", LastName = null };
        var result = TelegramHelper.UserEscaped(user);
        result.ShouldContain("\\*");
        result.ShouldContain("\\_");
        result.ShouldContain("\\`");
    }

    [Fact]
    public void MentionUsername_ReturnsCorrectFormat()
    {
        var user = new User { Id = 12345, FirstName = "John", LastName = null };
        var result = TelegramHelper.MentionUsername(user);
        result.ShouldStartWith("[От John](tg://user?id=12345)");
    }

    [Fact]
    public void NewPostIkm_HasCorrectButtons()
    {
        var ikm = TelegramHelper.NewPostIkm;
        ikm.InlineKeyboard.Count().ShouldBe(1);
        ikm.InlineKeyboard.First().Count().ShouldBe(2);
        ikm.InlineKeyboard.First().First().CallbackData.ShouldBe("+");
        ikm.InlineKeyboard.First().Skip(1).First().CallbackData.ShouldBe("-");
    }
}
