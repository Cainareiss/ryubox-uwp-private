#include "../src/Ryujinx.Uwp.Native/D3D12DescriptorRing.h"

void TestDescriptorRing()
{
    D3D12DescriptorRing ring(8);
    uint32_t slot = 999;
    Require(ring.Allocate(5,1,slot) && slot==0,"ring first range");
    Require(!ring.Allocate(4,1,slot) && ring.Used()==5,"unsubmitted range protected");
    ring.Collect(0);
    Require(!ring.Allocate(4,2,slot),"delayed GPU protected");
    ring.Collect(UINT64_MAX);
    Require(ring.Used()==5,"device removal does not retire descriptors");
    ring.Collect(1);
    Require(ring.Allocate(4,2,slot) && slot==0,"wrap after fence");
    Require(ring.Allocate(4,2,slot) && slot==4,"exact capacity");
    Require(!ring.Allocate(1,3,slot),"full ring rejection");
    ring.Collect(2);
    Require(ring.Used()==0 && !ring.Allocate(9,3,slot),"oversize rejection");
    Require(!ring.Allocate(1,UINT64_MAX,slot),"invalid retirement fence");
    // Independently track the last fence of every slot; artificial delayed
    // completion makes correctness deterministic on fast and slow GPUs.
    D3D12DescriptorRing stress(31);
    uint64_t owners[31]{};
    uint64_t completed=0, submission=1;
    uint32_t random=0x4812abc;
    for(int i=0;i<30000;++i)
    {
        random=random*1664525u+1013904223u;
        uint32_t count=1+(random%13);
        uint64_t before=stress.Used();
        if(stress.Allocate(count,submission,slot))
        {
            Require(slot+count<=31,"table is contiguous");
            for(uint32_t j=slot;j<slot+count;++j)
            {
                Require(owners[j]<=completed,"in-flight slot overwritten");
                owners[j]=submission;
            }
        }
        else Require(stress.Used()==before,"failed allocation is atomic");
        if(i%4==0) ++submission;
        if(i%7==0 && submission>3) completed=submission-3;
        stress.Collect(completed);
    }
    stress.Collect(submission);
    Require(stress.Used()==0 && stress.wraps>0,"stress retirement and wrap");
    std::cout << "PASS descriptor ring: 30000 delayed-fence allocations, unsubmitted protection, wrap, full, oversize, device removal\n";
}
