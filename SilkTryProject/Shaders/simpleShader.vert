#version 450

layout(location = 0) in vec3 position;
layout(location = 1) in vec3 color;

// 每帧由 vkCmdPushConstants 推进来的 MVP（model * view * projection）。
layout(push_constant) uniform Push {
	mat4 mvp;
} pc;

layout(location = 0) out vec4 fargColor;

void main() {
	gl_Position = pc.mvp * vec4(position, 1.0);
	fargColor = vec4(color, 1.0);
}
