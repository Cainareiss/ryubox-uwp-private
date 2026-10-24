// DsuCompanionStub.cpp - Stub for Xbox (no boost-asio needed)
#include <UwpImGuiFrontend/DsuCompanion.h>

namespace UwpImGuiFrontend {

class StubCompanionService : public ICompanionService
{
public:
	void Initialize(ICompanionHost&, std::string, std::string) override {}
	void Shutdown() override {}
	void AttachTransport(IExtensionPacketTransport*) override {}
	void DetachTransport(IExtensionPacketTransport*) override {}
	void UpdateRunningContent(RunningContent) override {}
	void UpdateSelection(ItemId) override {}
	std::optional<ItemId> TakePendingLaunch() override { return std::nullopt; }
	std::optional<ItemId> TakePendingSelection() override { return std::nullopt; }
	void CompletePendingLaunch(bool) override {}
	NavigationState GetNavigationState() const override { return {}; }
	CompanionStatus GetStatus() const override { return {}; }
	bool HandleExtensionPacket(std::span<const std::uint8_t>,
		std::vector<std::vector<std::uint8_t>>&) override { return false; }
};

std::unique_ptr<ICompanionService> CreateCompanionService()
{
	return std::make_unique<StubCompanionService>();
}

} // namespace UwpImGuiFrontend
