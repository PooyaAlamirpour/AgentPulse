using AgentPulse.Application.ChatModels;

namespace AgentPulse.Application.ModelRequests;

public interface IChatModelRequestBuilder
{
    ChatModelRequest Build(ChatModelRequestBuildInput input);
}
