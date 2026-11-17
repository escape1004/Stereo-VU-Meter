# Stereo VU Meter for XENEON EDGE

CORSAIR XENEON EDGE용 iCUE 오디오 위젯입니다. Windows 기본 입력 또는 출력 신호를 기본 스펙트럼 또는 아날로그 랙 테마로 표시합니다. 선택형 미디어 패널은 앨범 아트와 재생 제어를 제공합니다.

## 설치

Windows와 iCUE 5.52 이상이 필요합니다.

1. 배포 ZIP을 압축 해제하고 `Enable-Audio-Bridge-Startup.cmd`를 실행합니다.
2. iCUE의 XENEON EDGE 위젯 페이지에서 `Stereo-VU-Meter.icuewidget`을 가져옵니다.
3. 위젯에서 START를 누릅니다. 오디오 소스는 위젯 설정에서 선택할 수 있습니다.

재생 앱이 Windows 미디어 세션에 곡 정보와 재생 위치를 제공하면 미디어 기능을 사용할 수 있습니다.

## 오디오 브리지 자동 시작과 대기 상태

`Enable-Audio-Bridge-Startup.cmd`를 한 번 실행하면 오디오 브리지가 설치되어 즉시 대기 상태로 실행됩니다. 이후에는 현재 Windows 계정에 로그인할 때마다 백그라운드에서 자동으로 시작되므로 배치 파일을 다시 실행할 필요가 없습니다.

위젯에서 **START**를 누르면 오디오 캡처와 스펙트럼 분석이 시작됩니다. **STOP**을 누르거나 전원 버튼으로 VU 미터를 끄면 캡처와 분석이 멈추고 브리지는 대기 상태로 돌아갑니다. 대기 중에는 요청이 들어올 때만 응답하므로 오디오 처리에 CPU를 계속 사용하지 않습니다.

자동 시작을 해제하고 브리지도 종료·제거하려면 `Disable-Audio-Bridge-Startup.cmd`를 실행하세요.
