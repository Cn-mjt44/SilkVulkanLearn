#version 450

layout(location = 0) in vec2 position;
layout(location = 1) in vec3 color;

layout(location = 0) out vec4 fargColor;

void main() {
	gl_Position = vec4(position, 0.0, 1.0);
	fargColor = vec4(color, 1.0);
}
