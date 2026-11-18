# 로컬 발송 공급자 독립 계약 검증

```powershell
dotnet run --project tests/delivery-adapter-provider/DeliveryProviderTests.csproj -c Release -- verification-results/delivery-provider-contracts.json
```

Backend의 실제 LocalDeliveryProvider·Delivery/Notification·공유 계약 소스를 링크한다.
공통 Backend bin/obj, Oracle, Compose를 사용하지 않는다. 결과는60단언PASS다.

- HTTP는 가짜 HttpMessageHandler로 실제 공급자 호출을 검사한다.
  고정 주소/headers/이스케이프/서식·미리보기차단,응답200본문,1KiB상한(unknown길이포함),
  429/5xx,영구거절/리디렉션,Retry-After상한,전체timeout/취소,잘못된목적지/내용을 검사한다.
  예상 외 공급자/응답 스트림 예외는 LOCAL_PROVIDER_ERROR 영구실패로,4000자 본문의
  이스케이프 확장 한계는 네트워크 전 LOCAL_CONTENT_LIMIT로 분류한다.
- SMTP 오류는 실제 loopback TCP peer와 MailKit4.16.0이 통신한다.
  421/450/451/452/500/530/535/550/552/554 응답이 실제 MailKit 예외로 변환되고,
  공급자와 동일한 상태 코드 분류기가 일시/영구 결과로 변환하는지 검사한다.
  530은 MailKit가 ServiceNotAuthenticatedException으로 변환하므로 이 특수 동작도 확인한다.
- SMTP 성공 전송/Message-ID/QUIT 처리와 Mailpit 보관은 전체 로컬 통합 검증 대상이다.
  이 단독 시험이 고정 mailpit 호스트의 실제 발송 성공을 검증했다고 주장하지 않는다.
- 외부 이메일/채팅 호출0,DB접속0이다. SMTP Message-ID가 서버의 중복 억제를 보장하지 않는다.

이 프로젝트는 의존성을4.16.0/lockfile로 고정하고 캐시된 패키지로 독립 실행할 때
외부 NuGet audit 조회를 하지 않는다. 실제 Backend의 패키지 감사·복원 설정은 변경하지 않는다.

