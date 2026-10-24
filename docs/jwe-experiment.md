# JWE 加解密实验（本轮）

## 结论
微软给 Minecraft 启动器的 access_token 是 **JWE 加密**（5 段、header alg=dir / enc=A256GCM）而不是 JWT。
所以「拿到 token 就能用」这条最容易想到的路直接封死 —— 必须先把里面那个 AES 密钥抠出来。

## 已经到手的东西
- `C:\Users\yuwei\AppData\Roaming\.pcllike\accounts.json` 里存着 RS256 签名的 token
  （AES 密钥是从那里注入进去的）。
- 探针 `probe-login` 已经跑通全链，refresh_token 存在
  `C:\Users\yuwei\Documents\pcl2-like\.cache\probe-login-token.json`。

## 下一步要做的
1. 从 accounts.json 里解出注入用的那个 AES 密钥。
2. 用它把 JWE 拆开，验证里面确实是可用的 Minecraft access_token。
3. 把这条解密链引进启动器，让登录能真正跑完。