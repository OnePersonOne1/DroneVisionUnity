# 팀원용 셋업 가이드 (DroneVisionUnity)

이 문서는 **Git/GitHub/Python/Unity 가 처음**인 팀원이 이 프로젝트를 자기 컴퓨터에서 처음부터 실행하기까지의 모든 절차를 담은 단계별 안내서다. 위에서부터 **순서대로** 따라 하면 된다. 한 단계를 건너뛰면 그다음이 안 된다.

> **표기 규칙**
> - `▶ 명령어`: 터미널/PowerShell 에 그대로 복사·붙여넣어 실행한다.
> - `<...>` : 본인 값으로 바꿔 입력하는 자리표시자(예: `<유저이름>` → `gildong`). 꺾쇠는 빼고 쓴다.
> - **Windows** 사용자는 "명령 프롬프트(cmd)" 가 아니라 **PowerShell** 또는 **Git Bash** 를 사용한다 (둘 중 아무거나).
> - **macOS / Linux** 사용자는 기본 "터미널" 을 쓴다.

---

## 목차

1. [필수 프로그램 설치](#1-필수-프로그램-설치)
2. [GitHub 계정 + Git 인증 설정](#2-github-계정--git-인증-설정)
3. [프로젝트 클론(다운로드)](#3-프로젝트-클론다운로드)
4. [대용량 자산 받기 (모델 가중치 · 데모 세션)](#4-대용량-자산-받기-모델-가중치--데모-세션)
5. [Python 가상환경 + 패키지 설치](#5-python-가상환경--패키지-설치)
6. [Unity 프로젝트 열기](#6-unity-프로젝트-열기)
7. [파이프라인 실행 (오프라인 재생)](#7-파이프라인-실행-오프라인-재생)
8. [Git 일상 사용법 (pull / branch / commit / push)](#8-git-일상-사용법-pull--branch--commit--push)
9. [자주 막히는 문제 (트러블슈팅)](#9-자주-막히는-문제-트러블슈팅)
10. [참고 문서](#10-참고-문서)

---

## 1. 필수 프로그램 설치

**순서대로** 5 개 프로그램을 깔아야 한다. **Unity Hub 와 Unity 본체는 다른 프로그램**이라는 점을 잊지 말자.

### 1-1. Git

- **Windows**: <https://git-scm.com/download/win> 에서 64-bit Git for Windows Setup 다운로드 후 설치. 설치 옵션은 전부 기본값으로 두면 된다. 설치하면 **"Git Bash"** 라는 검은 터미널이 같이 깔린다.
- **macOS**: 터미널에서 다음 실행.
  ```bash
  ▶ xcode-select --install
  ```
- **Linux (Ubuntu)**:
  ```bash
  ▶ sudo apt update && sudo apt install -y git
  ```

설치 확인:
```bash
▶ git --version
```
`git version 2.xx.x` 형식이 나오면 성공.

### 1-2. Python 3.11

- **Windows**: <https://www.python.org/downloads/release/python-3119/> 페이지 하단의 "Windows installer (64-bit)" 다운로드 후 설치. ⚠️ **설치 첫 화면의 "Add python.exe to PATH" 체크박스 반드시 켠다.**
- **macOS**:
  ```bash
  ▶ brew install python@3.11
  ```
  (Homebrew 없으면 <https://brew.sh> 에서 먼저 설치)
- **Linux (Ubuntu)**:
  ```bash
  ▶ sudo apt install -y python3.11 python3.11-venv python3-pip
  ```

설치 확인:
```bash
▶ python --version       # Windows
▶ python3.11 --version   # macOS / Linux
```
`Python 3.11.x` 가 나와야 한다.

### 1-3. Unity Hub

<https://unity.com/download> 에서 "Download for Windows/Mac/Linux" 클릭 → Unity Hub 설치. **이건 런처일 뿐 엔진 본체가 아니다.**

### 1-4. Unity Editor 6000.4.0f1

이 프로젝트는 **정확히 이 버전**이 필요하다. 다른 버전으로 열면 프로젝트가 깨질 수 있다.

1. Unity Hub 실행.
2. 좌측 **Installs** 탭 → 우상단 **Install Editor** 클릭.
3. 상단 **Archive** 탭 → 화면 안내대로 "download archive" 를 눌러 브라우저에서 버전 목록 페이지를 연다.
4. **6000.4.0f1** 찾기 (없으면 `Unity 6.x` 섹션을 펼친다). 페이지의 "Unity Hub" 버튼 클릭 → 자동으로 Hub 에서 설치 다이얼로그가 뜬다.
5. 모듈 선택 화면에서 다음 항목 **체크**:
   - ✅ **Visual Studio** (또는 본인이 쓰는 IDE 모듈) — 코드 편집기 연동용.
   - (선택) 한국어 언어 팩.
   - 그 외 빌드 타겟(Android/iOS 등)은 본인 필요 시.
6. 설치 시작 → 2 ~ 8 GB 다운로드, 10 ~ 30 분 소요.

### 1-5. 코드 에디터 (Visual Studio Code 권장)

<https://code.visualstudio.com/> 에서 설치. 위 Unity 모듈로 같이 깔리는 "Visual Studio Community" 와는 다른 프로그램이지만, Python 스크립트 편집·디프 확인·간단한 Git 조작에 더 편리하다.

---

## 2. GitHub 계정 + Git 인증 설정

### 2-1. 계정 만들기

1. <https://github.com> 우측 상단 **Sign up** → 이메일·비밀번호로 계정 생성.
2. 가입 후 이메일 인증을 마친다.

### 2-2. 본인 식별 정보(전역 한 번만 설정)

이건 컴퓨터에 단 한 번만 하면 된다. 깃이 커밋에 찍는 이름·이메일을 알려주는 거다.

```bash
▶ git config --global user.name "<본인 이름>"
▶ git config --global user.email "<github 가입 이메일>"
```

예시:
```bash
▶ git config --global user.name "Gildong Hong"
▶ git config --global user.email "gildong@example.com"
```

### 2-3. Personal Access Token (PAT) 발급

GitHub 는 비밀번호로 push 를 못 한다. 대신 **토큰**(긴 비밀번호 같은 것)을 쓴다.

1. GitHub 로그인 → 우상단 본인 프로필 사진 → **Settings**.
2. 좌측 맨 아래 **Developer settings** → **Personal access tokens** → **Tokens (classic)** → **Generate new token (classic)**.
3. 설정:
   - **Note**: `DroneVisionUnity` (아무 메모나 OK).
   - **Expiration**: `90 days` 권장 (만료 시 재발급).
   - **Scopes**: ✅ `repo` 만 체크.
4. **Generate token** 클릭 → `ghp_xxxx...` 로 시작하는 토큰이 한 번만 보인다. **반드시 메모장 등에 복사**해 둔다. 이 페이지를 닫으면 다시는 못 본다.

### 2-4. 저장소 접근 권한 받기

저장소 소유자(팀장)에게 본인 GitHub 아이디를 알려주고 **Collaborator** 로 등록해 달라고 요청한다. 등록 후 본인 메일로 초대 링크가 오면 수락한다. (등록 안 돼 있으면 push 가 안 된다. clone 자체는 public 저장소면 등록 없이도 된다.)

---

## 3. 프로젝트 클론(다운로드)

### 3-1. 작업 폴더 선택

프로젝트를 둘 곳을 정한다. **경로에 한글·공백·특수문자가 없는 곳**을 강력 권장. 예:
- Windows: `C:\dev\`
- macOS/Linux: `~/dev/` (`~` 는 홈 디렉토리)

폴더가 없으면 만든다:
```bash
▶ mkdir -p ~/dev      # macOS / Linux
▶ mkdir C:\dev        # Windows (PowerShell)
```

해당 폴더로 이동:
```bash
▶ cd ~/dev            # macOS / Linux
▶ cd C:\dev           # Windows
```

### 3-2. clone 실행

```bash
▶ git clone https://github.com/OnePersonOne1/DroneVisionUnity.git
```

처음 push/pull/clone 시 GitHub 인증창이 뜨면:
- **Username**: 본인 GitHub 아이디.
- **Password**: ⚠️ **GitHub 비밀번호 아님 — 위 2-3 에서 만든 토큰(`ghp_...`) 을 붙여넣는다.**

clone 이 끝나면 현재 폴더에 `DroneVisionUnity/` 가 생긴다. 그 안으로 이동.

```bash
▶ cd DroneVisionUnity
```

확인:
```bash
▶ ls          # macOS / Linux / Git Bash
▶ dir         # Windows PowerShell
```
`README.md`, `Unity/`, `Python/`, `Models/` 등이 보여야 정상.

---

## 4. 대용량 자산 받기 (모델 가중치 · 데모 세션)

GitHub 는 100 MB 이상 파일을 못 올려서, 일부 자산은 Google Drive 에 따로 둔다.

### 4-1. RF-DETR 모델 가중치 (약 485 MB)

1. 브라우저로 <https://drive.google.com/file/d/1hoCFljPKaiiLfD9MdyfQ1XfUVvRozeyT/view?usp=drive_link> 접속.
2. 우상단 ⬇ 다운로드 버튼 → `checkpoint_best_total.pth` 다운로드.
3. 다운받은 파일을 **클론한 프로젝트의 `Models/` 폴더**로 옮긴다.
   - 최종 경로 예: `~/dev/DroneVisionUnity/Models/checkpoint_best_total.pth`

### 4-2. 데모 캡처 세션 (약 145 MB, **재생 모드용**)

라이브 카메라 없이 미리 녹화된 데이터로 동작 확인하려면 필요.

1. <https://drive.google.com/file/d/1sTiMGBD5R_6A1fty9yYbVERDCs7U_Hkx/view?usp=drive_link> 에서 zip 다운로드.
2. 프로젝트의 `output/` 폴더 안에서 압축 해제. 결과:
   ```
   DroneVisionUnity/output/20260529_122735/
                            ├── image/
                            ├── sensor/
                            ├── detect_offline/
                            └── projection/
                                └── projection_20260529_122735.csv
   ```

---

## 5. Python 가상환경 + 패키지 설치

**가상환경(venv)** 은 이 프로젝트 전용 파이썬 패키지 보관소다. 시스템 파이썬을 더럽히지 않게 분리한다.

### 5-1. venv 만들기

프로젝트 루트(`DroneVisionUnity/`)에서:

```bash
▶ python -m venv .venv        # Windows
▶ python3.11 -m venv .venv    # macOS / Linux
```

`.venv/` 폴더가 생긴다.

### 5-2. venv 활성화

이 명령은 **새 터미널을 열 때마다** 다시 해야 한다.

- **Windows (PowerShell)**:
  ```powershell
  ▶ .\.venv\Scripts\Activate.ps1
  ```
  만약 "실행 정책" 오류 뜨면 한 번만:
  ```powershell
  ▶ Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned
  ```
- **Windows (Git Bash / cmd)**:
  ```bash
  ▶ source .venv/Scripts/activate    # Git Bash
  ▶ .venv\Scripts\activate.bat       # cmd
  ```
- **macOS / Linux**:
  ```bash
  ▶ source .venv/bin/activate
  ```

성공하면 프롬프트 앞에 `(.venv)` 가 붙는다.

### 5-3. 패키지 설치

```bash
▶ pip install --upgrade pip
▶ pip install rfdetr supervision opencv-python numpy pillow
```

`info_service`(건물 정보 백엔드 — B 키 건물 카드, `;`/`'` 키 화재 상황 판단) 도 쓰려면 추가로:
```bash
▶ pip install -r Python/info_service/requirements.txt
```

설치는 10 ~ 30 분 걸릴 수 있다(특히 `rfdetr` 와 그 의존성인 PyTorch).

### 5-5. (선택) Kakao Local API 키 환경변수

`info_service` 의 건물명/주소 보강은 Kakao Local API 를 쓴다. 키 없이도 동작하지만 startup 경고와 함께 "이름 미상 건물" 비중이 늘어난다. 키는 **코드/파일에 하드코딩 금지** — 환경변수로만.

- 일회 사용(셸 export):
  ```bash
  ▶ export KAKAO_REST_API_KEY=<발급키>          # macOS / Linux / Git Bash
  ▶ $env:KAKAO_REST_API_KEY="<발급키>"           # Windows PowerShell
  ```
- docker compose 환경이면 `.env` 파일 사용 (gitignored — 자동 비공개). `.env.example` 을 `.env` 로 복사 후 본인 키 채우기:
  ```bash
  ▶ cp .env.example .env
  # .env 를 열어 KAKAO_REST_API_KEY=... 채워 넣기
  ```
  `docker compose up` 이 자동으로 env 주입.

키 발급은 <https://developers.kakao.com> → 앱 생성 → "REST API 키" 복사. 무료 한도 충분.

### 5-4. 설치 확인

```bash
▶ python -c "import rfdetr, cv2, numpy; print('OK')"
```
`OK` 만 출력되면 성공.

---

## 6. Unity 프로젝트 열기

### 6-1. Unity Hub 에 프로젝트 추가

1. Unity Hub 실행 → 좌측 **Projects** 탭.
2. 우상단 **Add** (또는 **Open**) → **Add project from disk**.
3. 클론한 `DroneVisionUnity/Unity` **폴더 자체**를 선택 (그 안의 `Assets/` 가 아니라 부모인 `Unity/`).
4. 프로젝트 목록에 `Unity` 항목이 추가된다. 옆에 Unity 버전이 `6000.4.0f1` 로 자동 표시되면 OK. 다른 버전으로 표시되면 좌측 버전 드롭다운에서 6000.4.0f1 선택.
5. 항목을 클릭해 연다. **첫 실행은 5 ~ 15 분 소요** (의존성 임포트). 진행 바가 다 차고 에디터가 뜰 때까지 기다린다.

### 6-2. 메인 씬 열기

에디터가 열리면 좌하단 **Project** 창에서 `Assets/Scenes/SampleScene.unity` 더블 클릭.

### 6-3. Play 모드 진입

상단 중앙 ▶ 재생 버튼 클릭. Game view 가 활성화된다.

- 단일 모니터면 Game view 상단 좌측 드롭다운("Display 1") 으로 Display 1/2/3/4 전환해서 각 화면 확인.
- 키 조작은 [`조작법.md`](조작법.md) 참고.

---

## 7. 파이프라인 실행 (오프라인 재생)

라이브 카메라 없이 데모 세션으로 검출 → Unity 시각화 흐름을 확인하는 시나리오.

### 7-1. 필수: venv 활성화

새 터미널을 열었다면 5-2 의 활성화 명령을 다시 실행.

### 7-2. Python 으로 CSV → UDP 송신

새 터미널 1 개 열고 venv 활성화 후 프로젝트 루트에서:

```bash
▶ cd Python
▶ python replay_offline.py --session ../output/20260529_122735 --rate 5 --loop
```

- `--rate 5`: 초당 5 프레임 송신.
- `--loop`: 마지막 프레임 후 처음으로 다시 돌아감.

콘솔에 `[replay] sent frame ...` 로그가 나오면 송신 중.

### 7-3. Unity 에서 수신

Unity Editor 에서 Play 누르면 `ProjectionUdpReceiver` 가 UDP 9870 에서 패킷을 받고, 인천 맵 위에 검출 마커가 뜬다.

> CSV 파일을 직접 재생하는 방식은 `ProjectionReplay` 컴포넌트도 있다. 그 경우 인스펙터의 `Projection Csv Path` 에 **절대 경로** 입력 필요. 예:
> - Windows: `C:\dev\DroneVisionUnity\output\20260529_122735\projection\projection_20260529_122735.csv`
> - macOS/Linux: `/home/<유저>/dev/DroneVisionUnity/output/20260529_122735/projection/projection_20260529_122735.csv`

### 7-4. 종료

- Python: 터미널에서 `Ctrl + C`.
- Unity: 다시 ▶ 버튼 눌러 Play 모드 종료.

### 7-5. (선택) 건물 정보 + 화재 상황 판단 백엔드

Unity 의 **B 키 건물 카드**, **`;`/`'` 키 모의 화재 평가**, **검출 화재 자동 평가** 모두 `Python/info_service/info_server.py` 백엔드가 필요하다.

**서버 기동** — 새 터미널 열고 venv 활성화 후:

```bash
▶ cd Python/info_service
▶ python info_server.py
```

Kakao 키가 있으면 같이 (5-5 참고):

```bash
▶ KAKAO_REST_API_KEY=<발급키> python info_server.py
```

성공 시:
```
INFO:     Application startup complete.
INFO:     Uvicorn running on http://127.0.0.1:8077
[info_server] OSM 건물 5194개 (...)
[info_server] GIS 건물 2533개 (...)
[info_server] 119안전센터 57개 (...)
[info_server] Kakao=True | LLM=qwen2.5:14b avail=True
```

**Unity 신규 컴포넌트 배치** (씬에 한 번만):

| 컴포넌트 | 역할 | 부착 위치 |
|---|---|---|
| `FireSim` | `;`/`'`/Del 키로 모의 화재 주입 + 좌표 등록 | 빈 GameObject 1개 |
| `SimClock` | KST 가상 시계 (override 가능) | (SimClockUI 가 자동 생성) |
| `SimClockUI` | 시간대 프리셋 UI (Z 토글) | 빈 GameObject 1개 |
| `SituationAssessClient` | `/assess` HTTP 호출 + 검출 화재 자동 감지 | 빈 GameObject 1개 |
| `BriefingPanel` | 결과 카드 (F12 토글) | 빈 GameObject 1개 |

Hierarchy 에서 빈 GameObject 5개 만들어 (또는 1개에 모아) `Add Component` 로 각각 추가. 인스펙터 슬롯은 비워두면 자동으로 같은 씬의 다른 컴포넌트를 찾는다.

**동작 확인**:

1. Play 모드 진입.
2. `;` 키 누름 → FP 카메라 중앙(십자선) 위치에 빨간 capsule (fire 마커).
3. ~1초 후 `BriefingPanel` 이 자동으로 화재 평가 결과 표시:
   - 위험도 뱃지 (low/mid/high 색)
   - 시간대 라벨 (실시간 또는 SimClock override)
   - 한국어 브리핑 (LLM 가용 시) 또는 template 폴백
   - **가까운 119 안전센터 top-5** (거리·차종·대수)
   - **권장 차량** (✓ 충당 / ⚠ 부족)
4. Z 키로 SimClock 열어 시간대 바꿔 보기 (18:00 퇴근 vs 02:00 심야) → 같은 화재에 대해 occupancy/risk/briefing 이 바뀌는지 확인.
5. Del 키 누르면 모의 화재 일괄 제거.
6. RF-DETR 검출에서 `fire_region`/`smoke_region` 가 들어와도 `SituationAssessClient.includeDetectionFires` 가 ON 이면 자동으로 동일하게 평가됨.

**Ollama 사용** (선택):

`ollama serve` + `ollama pull qwen2.5:14b` 가 떠 있으면 자연스러운 한국어 브리핑이 합성됨. 안 떠 있어도 template briefing 으로 즉시 폴백 — 화재·시간대·건물·차량 룰 출력은 동일하게 작동.

---

## 8. Git 일상 사용법 (pull / branch / commit / push)

### 8-1. 최신 변경 받기 (작업 시작 전 매번)

```bash
▶ cd <DroneVisionUnity 경로>
▶ git status              # 내 작업 영역 깨끗한지 확인
▶ git pull origin main    # 원격 최신 변경 가져오기
```

`Already up to date.` 또는 변경 요약이 나오면 정상.

> ⚠️ **본인이 수정한 파일이 있는데 pull 하면 conflict** 가 날 수 있다. 그땐 본인 변경을 먼저 commit 하거나, 안 끝났으면 `git stash` 로 임시 보관 → `pull` → `git stash pop` 순서로 풀어낸다.

### 8-2. 작업 브랜치 만들기 (권장)

`main` 에 직접 commit 하지 말고 별도 브랜치에서 작업.

```bash
▶ git checkout -b <브랜치이름>
```

브랜치 이름은 짧고 알아보게: `feat/sensor-view`, `fix/agl-zero`, `docs/tutorial` 등.

### 8-3. 변경 사항 확인

```bash
▶ git status              # 어떤 파일이 바뀌었는지
▶ git diff                # 어떤 내용이 바뀌었는지
```

### 8-4. 스테이지 + 커밋

```bash
▶ git add <파일1> <파일2>           # 특정 파일만
▶ git add .                         # 현재 폴더 이하 전부 (편하지만 위험)
▶ git commit -m "<메시지>"
```

커밋 메시지는 영문/한글 자유. 간결하고 동작 중심으로:
- ❌ `수정` (뭘 수정했는지 모름)
- ✅ `드론 AGL 라벨 추가`
- ✅ `센서 시점 카메라 거꾸로 보정`

### 8-5. 원격에 push

```bash
▶ git push origin <브랜치이름>      # 본인 브랜치라면
▶ git push origin main              # main 에서 직접 작업했다면
```

처음 push 시 인증창에서 GitHub 아이디 + **PAT(`ghp_...`)** 입력. macOS 는 키체인에, Windows 는 자격증명 관리자에 저장돼 다음부턴 자동.

### 8-6. Pull Request (브랜치 → main 병합 요청)

1. push 후 GitHub 저장소 페이지 접속.
2. 노란 배너에 **"Compare & pull request"** 버튼이 뜬다. 클릭.
3. 제목·설명 작성 후 **Create pull request**.
4. 동료가 리뷰·승인 후 **Merge pull request** 클릭하면 본인 변경이 `main` 으로 들어간다.

### 8-7. 자주 쓰는 명령 요약

| 명령 | 동작 |
|---|---|
| `git status` | 변경된 파일 목록 |
| `git diff` | 변경 내용 자세히 |
| `git log --oneline -20` | 최근 커밋 20 개 |
| `git checkout <브랜치>` | 브랜치 전환 |
| `git checkout -b <브랜치>` | 새 브랜치 만들고 전환 |
| `git branch -a` | 모든 브랜치 보기 |
| `git pull origin main` | 원격 최신 받기 |
| `git push origin <브랜치>` | 원격으로 보내기 |
| `git stash` / `git stash pop` | 변경 임시 보관 / 복원 |
| `git restore <파일>` | 파일 변경 취소 (⚠️ 되돌릴 수 없음) |

---

## 9. 자주 막히는 문제 (트러블슈팅)

### Q1. `git clone` 시 "Authentication failed"

- A. 비밀번호 대신 **PAT(`ghp_...`)** 을 입력. 2-3 참고.

### Q2. `pip install` 이 너무 느리거나 실패

- A. 인터넷 환경에 따라 5 ~ 30 분 걸릴 수 있다. 끊기면 같은 명령 재실행.
- A. 회사 망에서 SSL 오류면 모바일 핫스팟으로 재시도.

### Q3. Unity 가 `Failed to open project` / 임포트 무한 진행

- A. Unity 버전이 다른 경우. Hub 에서 6000.4.0f1 설치돼 있는지 확인.
- A. 클론 경로에 **한글/공백**이 있으면 깨질 수 있다. `C:\dev\` 같은 짧은 영문 경로로 옮긴다.
- A. `Unity/Library/` 폴더 삭제 후 재실행하면 처음부터 임포트한다(시간은 더 걸리지만 깨끗).

### Q4. Play 했는데 마커가 안 보임

- A. Python `replay_offline.py` 가 실제로 송신 중인지 콘솔 확인.
- A. UDP 포트 9870 이 방화벽에 막혀 있을 수 있다. Windows Defender 방화벽에서 Unity 와 Python 둘 다 통신 허용.
- A. `CubeGPSDisplay.altitudeReferenceBuilding` 슬롯이 비어 있으면 스케일이 0 이 돼 마커가 카메라 밖으로 튄다. 인스펙터에서 기준 건물(예: `finally.fbx` 자식 중 하나) 지정.

### Q5. Display 4 가 안 보임

- A. Editor 단일 모니터면 Game view 상단의 디스플레이 드롭다운에서 **"Display 4"** 직접 선택.
- A. 씬에 `SensorViewMode` 컴포넌트가 부착돼 있는지 확인. 부착 후 Play 중 `U` 키로 활성화.

### Q6. `git push` 가 거부됨 ("Updates were rejected")

- A. 다른 사람이 먼저 push 한 상태. `git pull origin main --rebase` 로 최신을 받은 뒤 다시 push.

### Q7. 커밋에 빼야 할 파일을 실수로 추가함

- A. push 전이면:
  ```bash
  ▶ git restore --staged <파일>      # 스테이지에서 제거 (변경은 유지)
  ```
- A. 이미 push 됐으면 팀장에게 알려 강제 푸시 여부 결정. **혼자 `--force` 금지.**

### Q8. `(.venv)` 표시가 없는데 `pip install` 하면?

- A. **시스템 파이썬에 깔린다.** 반드시 venv 활성화(`activate`) 후 진행. 잘못 깔았으면 venv 안에서 다시 `pip install` 하면 됨.

### Q9. CLAUDE.md / .claude/ 가 git status 에 안 나옴

- A. 정상. `.gitignore` 에 등록돼 추적 대상이 아니다. 건드리지 말 것.

### Q10. `python` 명령이 없다고 함 (Windows)

- A. 설치 시 "Add python.exe to PATH" 체크 누락. 파이썬 재설치하거나, **Python launcher** 가 깔려 있으면 `py -3.11` 로 대체.

### Q11. B 키 건물 카드 / `;` 화재 BriefingPanel 이 안 뜸

- A. `info_server.py` 가 떠 있는지 확인. `▶ curl localhost:8077/health` 가 JSON 반환해야 정상. 안 뜨면 7-5 절차로 기동.
- A. Unity Console 에 `[InfoService] /building_info 실패: Cannot connect to destination host` 같은 메시지 있으면 백엔드 미가동. 위와 동일.
- A. BriefingPanel 이 "대기 중" 메시지에서 안 바뀌면: 씬에 `BuildingInfoProbe`, `BuildingInfoService`, `FireSim`, `SituationAssessClient`, `BriefingPanel` 다 부착됐는지 확인. 없으면 추가 (7-5).
- A. Ollama 가 hang(GPU busy 등)이면 briefing_source 가 `template` 로 자동 폴백 — 한 번 실패 후 60초간 LLM 호출 skip (`INFO_LLM_FAIL_COOLDOWN` 으로 조정). 자연어 브리핑이 안 나와도 룰 기반 정보(차량·top-5·risk)는 정상 표시됨.

### Q12. info_server 가 `address already in use` 로 못 뜸

- A. 8077 포트를 다른 프로세스가 점유 중. 종료:
  ```bash
  ▶ pkill -f "python.*info_server.py"        # macOS / Linux
  ```
  또는 PID 직접 종료: `▶ lsof -i :8077` (macOS/Linux) / `▶ Get-NetTCPConnection -LocalPort 8077` (Windows) 으로 PID 찾아 kill.

---

## 10. 참고 문서

- [`README.md`](README.md) — 프로젝트 개요·아키텍처·자산 다운로드 링크
- [`조작법.md`](조작법.md) — Unity 플레이 모드 키 매핑 전체
- [`Unity/PIPELINE_MERGE_NOTES.md`](Unity/PIPELINE_MERGE_NOTES.md) — GPS↔월드 좌표 변환 규약 (개발자용)
- Git 공식 입문서 (한글): <https://git-scm.com/book/ko/v2>
- GitHub Hello World: <https://docs.github.com/ko/get-started/start-your-journey/hello-world>

질문은 팀 슬랙/노션 등 정해진 채널에 올린다. 막힌 명령·에러 메시지는 **전문 텍스트 그대로** 캡처해서 공유하면 답변이 빠르다.
