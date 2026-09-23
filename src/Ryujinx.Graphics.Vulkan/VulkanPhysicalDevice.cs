using Ryujinx.Common.Utilities;
using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Vulkan
{
    readonly struct VulkanPhysicalDevice
    {
        public readonly PhysicalDevice PhysicalDevice;
        public readonly PhysicalDeviceFeatures PhysicalDeviceFeatures;
        public readonly PhysicalDeviceProperties PhysicalDeviceProperties;
        public readonly PhysicalDeviceMemoryProperties PhysicalDeviceMemoryProperties;
        public readonly QueueFamilyProperties[] QueueFamilyProperties;
        public readonly string DeviceName;
        public readonly IReadOnlySet<string> DeviceExtensions;

        public VulkanPhysicalDevice(Vk api, PhysicalDevice physicalDevice, Action<string> diagnostic = null)
        {
            PhysicalDevice = physicalDevice;
            diagnostic?.Invoke("Vulkan device inspection: features");
            PhysicalDeviceFeatures = api.GetPhysicalDeviceFeature(PhysicalDevice);

            diagnostic?.Invoke("Vulkan device inspection: properties");
            api.GetPhysicalDeviceProperties(PhysicalDevice, out PhysicalDeviceProperties physicalDeviceProperties);
            PhysicalDeviceProperties = physicalDeviceProperties;

            diagnostic?.Invoke("Vulkan device inspection: memory-properties");
            api.GetPhysicalDeviceMemoryProperties(PhysicalDevice, out PhysicalDeviceMemoryProperties);

            unsafe
            {
                DeviceName = Marshal.PtrToStringAnsi((nint)physicalDeviceProperties.DeviceName);
            }

            uint propertiesCount = 0;

            diagnostic?.Invoke("Vulkan device inspection: queue-family-count");
            api.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, SpanHelpers.AsSpan(ref propertiesCount), Span<QueueFamilyProperties>.Empty);

            QueueFamilyProperties = new QueueFamilyProperties[propertiesCount];

            diagnostic?.Invoke($"Vulkan device inspection: queue-families={propertiesCount}");
            api.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, SpanHelpers.AsSpan(ref propertiesCount), QueueFamilyProperties);

            diagnostic?.Invoke("Vulkan device inspection: extension-count");
            api.EnumerateDeviceExtensionProperties(PhysicalDevice, Span<byte>.Empty, SpanHelpers.AsSpan(ref propertiesCount), Span<ExtensionProperties>.Empty).ThrowOnError();

            ExtensionProperties[] extensionProperties = new ExtensionProperties[propertiesCount];

            diagnostic?.Invoke($"Vulkan device inspection: extensions={propertiesCount}");
            api.EnumerateDeviceExtensionProperties(PhysicalDevice, Span<byte>.Empty, SpanHelpers.AsSpan(ref propertiesCount), extensionProperties).ThrowOnError();

            unsafe
            {
                DeviceExtensions = extensionProperties.Select(x => Marshal.PtrToStringAnsi((nint)x.ExtensionName)).ToImmutableHashSet();
            }

            diagnostic?.Invoke($"Vulkan device inspection complete: {DeviceName}");
        }

        public string Id => $"0x{PhysicalDeviceProperties.VendorID:X}_0x{PhysicalDeviceProperties.DeviceID:X}";

        public bool IsDeviceExtensionPresent(string extension) => DeviceExtensions.Contains(extension);

        public unsafe bool TryGetPhysicalDeviceDriverPropertiesKHR(Vk api, out PhysicalDeviceDriverPropertiesKHR res)
        {
            if (!IsDeviceExtensionPresent("VK_KHR_driver_properties"))
            {
                res = default;

                return false;
            }

            PhysicalDeviceDriverPropertiesKHR physicalDeviceDriverProperties = new()
            {
                SType = StructureType.PhysicalDeviceDriverPropertiesKhr
            };

            PhysicalDeviceProperties2 physicalDeviceProperties2 = new()
            {
                SType = StructureType.PhysicalDeviceProperties2,
                PNext = &physicalDeviceDriverProperties
            };

            api.GetPhysicalDeviceProperties2(PhysicalDevice, &physicalDeviceProperties2);

            res = physicalDeviceDriverProperties;

            return true;
        }

        public DeviceInfo ToDeviceInfo()
        {
            return new DeviceInfo(
                Id,
                VendorUtils.GetNameFromId(PhysicalDeviceProperties.VendorID),
                DeviceName,
                PhysicalDeviceProperties.DeviceType == PhysicalDeviceType.DiscreteGpu);
        }
    }
}
