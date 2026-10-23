# G3 Tuner

โปรแกรมจูนเสียงสำหรับ DAC/แอมป์ USB **Creative Sound Blaster G3** บน Windows
ปรับเสียงหูฟังให้ตรงตามผลวัดจริง ทำให้ไมค์ชัดขึ้น และตัดเสียงรบกวนด้วย AI

*Tuning app for the Creative Sound Blaster G3 on Windows: headphone correction, microphone clarity and AI noise suppression. [English below](#english)*

> **สถานะ: ทดลอง** ระบบ EQ และการตัดเสียงรบกวนทำงานผ่าน Equalizer APO และยังไม่ได้ทดสอบครบทุกการตั้งค่าบนเครื่องจริง

## ความสามารถ

- **หูฟัง:** ค้นหารุ่นหูฟังจากฐานข้อมูล [AutoEq](https://github.com/jaakkopasanen/AutoEq) ได้กว่า 8,000 รุ่น แล้วใช้ EQ แก้เสียงให้แบนตามผลวัดจริง ปรับเบสและเสียงแหลมเพิ่มได้ และมีปุ่มเปิดปิดไว้ฟังเทียบ
- **ตัดเสียงรบกวน:** เลือกได้ระหว่าง
  - AI ในตัวโปรแกรม ใช้ [RNNoise](https://github.com/werman/noise-suppression-for-voice) ทำงานบน CPU ตั้งค่าได้ว่าจะให้ปิดไมค์อัตโนมัติตอนไม่พูดแรงแค่ไหน และหน่วงก่อนปิดนานเท่าไร
  - NVIDIA Broadcast สำหรับการ์ดจอ RTX
- **EQ ไมค์:** ตัดเสียงหึ่งย่านต่ำ ลดเสียงอู้ เพิ่มความคมของเสียงพูด และตัดเสียงซ่าย่านสูง มีพรีเซ็ตให้เลือก 4 แบบ
- **ทดสอบไมค์:** อัดเสียงแล้ววัดระดับเสียงพูด เสียงซ่า และ SNR อัดเทียบตอนเปิดกับปิดการตัดเสียงรบกวนได้ แล้วเปิดฟังเทียบกัน

## สิ่งที่ต้องมี

- Windows 10/11 แบบ 64-bit
- Creative Sound Blaster G3
- [Equalizer APO](https://sourceforge.net/projects/equalizerapo/) โดยตอนติดตั้งให้เลือกอุปกรณ์ Headset, Speakers และ Microphone ของ Sound Blaster G3
- (ไม่บังคับ) [NVIDIA Broadcast](https://www.nvidia.com/en-us/geforce/broadcasting/broadcast-app/) สำหรับการ์ดจอ RTX

## คอมไพล์

ไม่ต้องติดตั้ง SDK เพิ่ม เพราะใช้คอมไพเลอร์ C# ที่มากับ Windows (.NET Framework 4.x)

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

ถ้าเครื่องเปิด Smart App Control ไว้ ไฟล์ exe ต้องมีลายเซ็นดิจิทัลจากใบรับรองที่ CA ซึ่ง Microsoft รับรองออกให้ เมื่อติดตั้งใบรับรองในเครื่องแล้ว ให้คอมไพล์และเซ็นในคำสั่งเดียว:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Sign
```

## หลักการทำงาน

G3 Tuner เขียนไฟล์ `config\G3Tuner.txt` ในโฟลเดอร์ของ Equalizer APO แล้วให้ `config.txt` เรียกใช้ไฟล์นี้ ส่วน `config.txt` เดิมจะถูกสำรองไว้เป็น `config.txt.g3tuner-backup`

ลำดับการประมวลผลเสียงไมค์:

```
ไมค์ G3 → ตัดเสียงต่ำ → RNNoise → EQ เพิ่มความคม → เกม/Discord
```

---

## English

G3 Tuner is a small Windows app for the Creative Sound Blaster G3 that drives [Equalizer APO](https://sourceforge.net/projects/equalizerapo/). The UI is in Thai.

> **Status: experimental.** The EQ and noise suppression run through Equalizer APO and have not yet been verified on real hardware in every configuration.

- **Headphones:** applies [AutoEq](https://github.com/jaakkopasanen/AutoEq) parametric correction for 8,000+ models, with bass and treble trims and an on/off toggle for A/B listening.
- **Noise suppression:** either the [RNNoise VST](https://github.com/werman/noise-suppression-for-voice) inside Equalizer APO, with adjustable voice-activity gating and hold time, or NVIDIA Broadcast on RTX GPUs.
- **Mic EQ:** high-pass, mud cut, presence boost and hiss low-pass, with 4 presets.
- **Mic test:** records the mic and reports speech level, noise floor, SNR and peak. It can also compare noise suppression on and off.

Build with `build.ps1`, which uses the C# compiler that ships with Windows. Pass `-Sign` to also sign the exe with a code-signing certificate installed on the machine (see `sign.ps1`).

## Third-party components

None of these are bundled in this repository.

| Component | License | How it is used |
|---|---|---|
| [Equalizer APO](https://sourceforge.net/projects/equalizerapo/) | GPL-2.0-or-later | installed separately by the user |
| [AutoEq](https://github.com/jaakkopasanen/AutoEq) results | MIT | downloaded at runtime |
| [noise-suppression-for-voice](https://github.com/werman/noise-suppression-for-voice) (RNNoise VST) | GPL-3.0 | downloaded only when the user clicks install |

This project is not affiliated with or endorsed by Creative Technology, NVIDIA or Microsoft. Product names are trademarks of their respective owners.

## License

[MIT](LICENSE)
