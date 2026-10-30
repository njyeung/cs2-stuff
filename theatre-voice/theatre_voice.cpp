// TheatreVoice is a minimal Metamod:Source plugin that posts a network message whose
// protobuf bytes were built elsewhere. TheatreScreen (C#) serializes CSVCMsg_VoiceData
// itself, because CounterStrikeSharp cannot set nested protobuf fields such as
// audio.voice_data; this plugin only parses those bytes into the game's own message
// object and sends it on the voice channel. It knows nothing about voice or Opus.
#include <ISmmPlugin.h>
#include <engine/igameeventsystem.h>
#include <networksystem/inetworkmessages.h>
#include <networksystem/netmessage.h>
#include <google/protobuf/message.h>

class TheatreVoicePlugin final : public ISmmPlugin
{
public:
	bool Load(PluginId id, ISmmAPI *ismm, char *error, size_t maxlen, bool late) override;
	bool Unload(char *error, size_t maxlen) override;
	const char *GetAuthor() override { return "cs2-server"; }
	const char *GetName() override { return "TheatreVoice"; }
	const char *GetDescription() override { return "Posts prebuilt network messages for TheatreScreen"; }
	const char *GetURL() override { return "https://github.com/njyeung/cs2-stuff"; }
	const char *GetLicense() override { return "MIT"; }
	const char *GetVersion() override { return "0.1.0"; }
	const char *GetDate() override { return __DATE__; }
	const char *GetLogTag() override { return "THEATREVOICE"; }
};

static IGameEventSystem *g_gameEventSystem;
static INetworkMessages *g_networkMessages;

TheatreVoicePlugin g_ThisPlugin;
PLUGIN_EXPOSE(TheatreVoicePlugin, g_ThisPlugin);

bool TheatreVoicePlugin::Load(PluginId id, ISmmAPI *ismm, char *error, size_t maxlen, bool late)
{
	PLUGIN_SAVEVARS();
	GET_V_IFACE_CURRENT(GetEngineFactory, g_gameEventSystem, IGameEventSystem, GAMEEVENTSYSTEM_INTERFACE_VERSION);
	GET_V_IFACE_CURRENT(GetEngineFactory, g_networkMessages, INetworkMessages, NETWORKMESSAGES_INTERFACE_VERSION);
	return true;
}

bool TheatreVoicePlugin::Unload(char *error, size_t maxlen)
{
	g_gameEventSystem = nullptr;
	g_networkMessages = nullptr;
	return true;
}

// Parses size bytes of data as network message messageId and posts it to the players
// whose slots are set in recipients, on bufType (a NetChannelBufType_t; 2 is BUF_VOICE).
// Call it from the game thread. Returns 0 when posted, or a negative code:
// -1 plugin not loaded by Metamod, -2 unknown message id, -3 allocation failed,
// -4 bytes are not a valid message.
extern "C" __attribute__((visibility("default")))
int TheatreVoice_Post(int messageId, const uint8 *data, int size, uint64 recipients, int bufType)
{
	if (!g_gameEventSystem || !g_networkMessages)
		return -1;
	INetworkMessageInternal *info = g_networkMessages->FindNetworkMessageById(messageId);
	if (!info)
		return -2;
	CNetMessage *message = info->AllocateMessage();
	if (!message)
		return -3;
	if (!message->ToPB<google::protobuf::Message>()->ParseFromArray(data, size))
	{
		delete message;
		return -4;
	}
	g_gameEventSystem->PostEventAbstract(CSplitScreenSlot(0), false, ABSOLUTE_PLAYER_LIMIT, &recipients,
		info, message, 0, static_cast<NetChannelBufType_t>(bufType));
	delete message; // The virtual destructor frees through the game's own allocator.
	return 0;
}
