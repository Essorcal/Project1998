using System.Text;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

[Collection("db")]
public sealed class BoardWireTests
{
    [Theory]
    [InlineData(2005)]
    [InlineData(2006)]
    public void BoardAndMailRepliesKeepTheirLegacyBytes(int port)
    {
        string user = $"wire_{Guid.NewGuid():N}";
        int board = port == 2005 ? 0x7E01 : 0x7E02;
        var record = new RecordingOutbound();
        var session = new Session(record, port, new CharacterStore(RepoPaths.CharsDir()), new World(),
            new Character { Name = user, Level = 99 });
        BoardPost post = Boards.Post(board, "A", "T", "B", 1, 2);
        Assert.Equal(1, post.Id);
        Mail.Send(user, "A", "T", "B", 1, 2, -1, 0, 0);
        string boardHex = port == 2005 ? "7E01" : "7E02";

        // Unknown board name is empty; the row is color 0, id 1, author A, Jan 2, topic T.
        void AssertReply(string requestHex, string expectedHex)
        {
            record.Clear();
            session.Receive(SessionFixture.Frame(0x3B, Convert.FromHexString(requestHex)));
            Assert.Equal(Convert.FromHexString(expectedHex), Assert.Single(record.BodiesOf(0x31)));
        }

        AssertReply("02" + boardHex, "0203" + boardHex + "0001000001014101020154");
        AssertReply("03" + boardHex + "0001", "0303000001014101020154000142");
        AssertReply("020000", "04030000074D61696C626F780100000101410102032A2054");
        AssertReply("0300000001", "0503010001014101020154000142");
        AssertReply("020000", "04030000074D61696C626F7801000001014101020154");
        AssertReply("0500000001", "07011D" + Convert.ToHexString(Encoding.ASCII.GetBytes("The message has been deleted.")) + "07");
    }

    [Fact]
    public void BoardListKeepsItsHistoricalTitlePrefixAndMailboxOrder()
    {
        var record = new RecordingOutbound();
        var session = new Session(record, 2005, new CharacterStore(RepoPaths.CharsDir()), new World(),
            new Character { Name = "wire_list" });
        session.Receive(SessionFixture.Frame(0x3B, new byte[] { 1, 0 }));
        Assert.Equal(Convert.FromHexString(
            "010D4E65787573544B426F617264730A" +
            "000109436F6D6D756E697479" +
            "00020C4D61726B6574202842757929" +
            "00030D4D61726B6574202853656C6C29" +
            "00040748756E74696E67" +
            "000510436F6D6D756E697479204576656E7473" +
            "0006034C6177" + "0007054775696465" + "000806506F65747279" +
            "00090442756773" + "0000074D61696C626F78"), Assert.Single(record.BodiesOf(0x31)));
    }

    [Fact]
    public void OverlongStoredTopicRetainsItsExistingLengthCast()
    {
        string user = $"wire_long_{Guid.NewGuid():N}";
        var record = new RecordingOutbound();
        var session = new Session(record, 2005, new CharacterStore(RepoPaths.CharsDir()), new World(),
            new Character { Name = user });
        Mail.Send(user, "A", new string('T', 256), "B", 1, 2, -1, 0, 0);
        session.Receive(SessionFixture.Frame(0x3B, new byte[] { 3, 0, 0, 0, 1 }));
        byte[] body = Assert.Single(record.BodiesOf(0x31));
        Assert.Equal(Convert.FromHexString("05030100010141010200"), body[..10]);
        Assert.Equal(Enumerable.Repeat((byte)'T', 256), body[10..266]);
        Assert.Equal(Convert.FromHexString("000142"), body[266..]);
    }
}
